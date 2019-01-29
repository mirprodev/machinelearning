// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Data.DataView;
using Microsoft.ML.Internal.Utilities;
using Microsoft.ML.Model;
using Microsoft.ML.Model.Onnx;
using Microsoft.ML.Model.Pfa;
using Newtonsoft.Json.Linq;

namespace Microsoft.ML.Data
{
    /// <summary>
    /// Base class for transforms.
    /// </summary>
    public abstract class TransformBase : IDataTransform
    {
        protected readonly IHost Host;

        public IDataView Source { get; }

        protected TransformBase(IHostEnvironment env, string name, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckNonWhiteSpace(name, nameof(name));
            env.CheckValue(input, nameof(input));

            Host = env.Register(name);
            Source = input;
        }

        protected TransformBase(IHost host, IDataView input)
        {
            Contracts.CheckValue(host, nameof(host));
            host.CheckValue(input, nameof(input));

            Host = host;
            Source = input;
        }

        public abstract void Save(ModelSaveContext ctx);

        public abstract long? GetRowCount();

        public virtual bool CanShuffle { get { return Source.CanShuffle; } }

        /// <summary>
        /// The field is the type information of the produced IDataView of this transformer.
        ///
        /// Explicit interface implementation hides <see cref="IDataView.Schema"/> in all derived classes. The reason
        /// is that a transformer should know the type it will produce but shouldn't contain the type of the data it produces.
        /// Thus, this field will be eventually removed while legacy code can still access <see cref="IDataView.Schema"/> for now.
        /// </summary>
        Schema IDataView.Schema => OutputSchema;

        public abstract Schema OutputSchema { get; }

        public RowCursor GetRowCursor(IEnumerable<Schema.Column> columnsNeeded, Random rand = null)
        {
            Host.CheckValueOrNull(rand);

            var predicate = RowCursorUtils.FromColumnsToPredicate(columnsNeeded, OutputSchema);

            var rng = CanShuffle ? rand : null;
            bool? useParallel = ShouldUseParallelCursors(predicate);

            // When useParallel is null, let the input decide, so go ahead and ask for parallel.
            // When the input wants to be split, this puts the consolidation after this transform
            // instead of before. This is likely to produce better performance, for example, when
            // this is RangeFilter.
            RowCursor curs;
            if (useParallel != false &&
                DataViewUtils.TryCreateConsolidatingCursor(out curs, this, columnsNeeded, Host, rng))
            {
                return curs;
            }

            return GetRowCursorCore(columnsNeeded, rng);
        }

        /// <summary>
        /// This returns false when this transform cannot support parallel cursors, null when it
        /// doesn't care, and true when it benefits from parallel cursors. For example, a transform
        /// that simply affects metadata, but not column values should return null, while a transform
        /// that does a bunch of computation should return true (if legal).
        /// </summary>
        protected abstract bool? ShouldUseParallelCursors(Func<int, bool> predicate);

        /// <summary>
        /// Create a single (non-parallel) row cursor.
        /// </summary>
        protected abstract RowCursor GetRowCursorCore(IEnumerable<Schema.Column> columnsNeeded, Random rand = null);

        public abstract RowCursor[] GetRowCursorSet(IEnumerable<Schema.Column> columnsNeeded, int n, Random rand = null);
    }

    /// <summary>
    /// Base class for transforms that map single input row to single output row.
    /// </summary>
    public abstract class RowToRowTransformBase : TransformBase
    {
        protected RowToRowTransformBase(IHostEnvironment env, string name, IDataView input)
            : base(env, name, input)
        {
        }

        protected RowToRowTransformBase(IHost host, IDataView input)
            : base(host, input)
        {
        }

        public sealed override long? GetRowCount() { return Source.GetRowCount(); }
    }

    /// <summary>
    /// Base class for transforms that filter out rows without changing the schema.
    /// </summary>
    [BestFriend]
    internal abstract class FilterBase : TransformBase, ITransformCanSavePfa
    {
        [BestFriend]
        private protected FilterBase(IHostEnvironment env, string name, IDataView input)
            : base(env, name, input)
        {
        }

        [BestFriend]
        private protected FilterBase(IHost host, IDataView input)
            : base(host, input)
        {
        }

        public override long? GetRowCount() => null;

        public override Schema OutputSchema => Source.Schema;

        bool ICanSavePfa.CanSavePfa => true;

        void ISaveAsPfa.SaveAsPfa(BoundPfaContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            // Because filters do not modify the schema, this is a no-op.
        }
    }

    public abstract class RowToRowMapperTransformBase : RowToRowTransformBase, IRowToRowMapper
    {
        protected RowToRowMapperTransformBase(IHostEnvironment env, string name, IDataView input)
            : base(env, name, input)
        {
        }

        protected RowToRowMapperTransformBase(IHost host, IDataView input)
            : base(host, input)
        {
        }

        public Func<int, bool> GetDependencies(Func<int, bool> predicate)
        {
            return GetDependenciesCore(predicate);
        }

        protected abstract Func<int, bool> GetDependenciesCore(Func<int, bool> predicate);

        public Schema InputSchema => Source.Schema;

        public Row GetRow(Row input, Func<int, bool> active)
        {
            Host.CheckValue(input, nameof(input));
            Host.CheckValue(active, nameof(active));
            Host.Check(input.Schema == Source.Schema, "Schema of input row must be the same as the schema the mapper is bound to");

            using (var ch = Host.Start("GetEntireRow"))
            {
                var getters = CreateGetters(input, active, out Action disp);
                return new RowImpl(input, this, OutputSchema, getters, disp);
            }
        }

        protected abstract Delegate[] CreateGetters(Row input, Func<int, bool> active, out Action disp);

        protected abstract int MapColumnIndex(out bool isSrc, int col);

        private sealed class RowImpl : WrappingRow
        {
            private readonly Schema _schema;
            private readonly Delegate[] _getters;
            private readonly Action _disposer;

            private readonly RowToRowMapperTransformBase _parent;

            public override Schema Schema => _schema;

            public RowImpl(Row input, RowToRowMapperTransformBase parent, Schema schema, Delegate[] getters, Action disposer)
                : base(input)
            {
                _parent = parent;
                _schema = schema;
                _getters = getters;
                _disposer = disposer;
            }

            protected override void DisposeCore(bool disposing)
            {
                if (disposing)
                    _disposer?.Invoke();
            }

            public override ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                bool isSrc;
                int index = _parent.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return Input.GetGetter<TValue>(index);

                Contracts.Assert(_getters[index] != null);
                var fn = _getters[index] as ValueGetter<TValue>;
                if (fn == null)
                    throw Contracts.Except("Invalid TValue in GetGetter: '{0}'", typeof(TValue));
                return fn;
            }

            public override bool IsColumnActive(int col)
            {
                bool isSrc;
                int index = _parent.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return Input.IsColumnActive((index));
                return _getters[index] != null;
            }
        }
    }

    /// <summary>
    /// Base class for transforms that operate row by row with each destination column using one
    /// source column. It provides an extension mechanism to allow a destination column to depend
    /// on multiple input columns.
    /// This class provides the implementation of ISchema and IRowCursor.
    /// </summary>
    public abstract class OneToOneTransformBase : RowToRowMapperTransformBase, ITransposeDataView, ITransformCanSavePfa,
        ITransformCanSaveOnnx
    {
        /// <summary>
        /// Information about an added column - the name of the new column, the index of the
        /// source column and the type of the source column.
        /// </summary>
        public sealed class ColInfo
        {
            public readonly string Name;
            public readonly int Source;
            public readonly ColumnType TypeSrc;
            public readonly VectorType SlotTypeSrc;

            public ColInfo(string name, int colSrc, ColumnType typeSrc, VectorType slotTypeSrc)
            {
                Contracts.AssertNonEmpty(name);
                Contracts.Assert(colSrc >= 0);
                Contracts.AssertValue(typeSrc);
                Contracts.AssertValueOrNull(slotTypeSrc);
                Contracts.Assert(slotTypeSrc == null || typeSrc.GetItemType().Equals(slotTypeSrc.ItemType));

                Name = name;
                Source = colSrc;
                TypeSrc = typeSrc;
                SlotTypeSrc = slotTypeSrc;
            }
        }

        // The schema class for this transform. This delegates to the parent transform whatever
        // it can't figure out.
        private sealed class Bindings : ColumnBindingsBase
        {
            // The parent transform.
            private readonly OneToOneTransformBase _parent;

            /// <summary>
            /// Information about each added column.
            /// </summary>
            public readonly ColInfo[] Infos;

            public VectorType GetSlotType(int col)
            {
                var tidv = _parent.InputTranspose;
                return tidv?.GetSlotType(col);
            }

            private const string InvalidTypeErrorFormat = "Source column '{0}' has invalid type ('{1}'): {2}.";

            private Bindings(OneToOneTransformBase parent, ColInfo[] infos,
                Schema input, bool user, string[] names)
                : base(input, user, names)
            {
                Contracts.AssertValue(parent);
                Contracts.AssertValue(parent.Host);
                Contracts.Assert(Utils.Size(infos) == InfoCount);

                _parent = parent;
                Infos = infos;
            }

            public static Bindings Create(OneToOneTransformBase parent, OneToOneColumn[] column, Schema inputSchema,
               ITransposeDataView transposedInput, Func<ColumnType, string> testType)
            {
                Contracts.AssertValue(parent);
                var host = parent.Host;
                host.CheckUserArg(Utils.Size(column) > 0, nameof(column));
                host.AssertValue(inputSchema);
                host.AssertValueOrNull(transposedInput);
                host.AssertValueOrNull(testType);

                var names = new string[column.Length];
                var infos = new ColInfo[column.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var item = column[i];
                    host.CheckUserArg(item.TrySanitize(), nameof(OneToOneColumn.Name), "Invalid new column name");
                    names[i] = item.Name;

                    int colSrc;
                    if (!inputSchema.TryGetColumnIndex(item.Source, out colSrc))
                        throw host.ExceptUserArg(nameof(OneToOneColumn.Source), "Source column '{0}' not found", item.Source);

                    var type = inputSchema[colSrc].Type;
                    if (testType != null)
                    {
                        string reason = testType(type);
                        if (reason != null)
                            throw host.ExceptUserArg(nameof(OneToOneColumn.Source), InvalidTypeErrorFormat, item.Source, type, reason);
                    }

                    var slotType = transposedInput?.GetSlotType(i);
                    infos[i] = new ColInfo(names[i], colSrc, type, slotType as VectorType);
                }

                return new Bindings(parent, infos, inputSchema, true, names);
            }

            public static Bindings Create(OneToOneTransformBase parent, ModelLoadContext ctx, Schema inputSchema,
                ITransposeDataView transposeInput, Func<ColumnType, string> testType)
            {
                Contracts.AssertValue(parent);
                var host = parent.Host;
                host.CheckValue(ctx, nameof(ctx));
                host.AssertValue(inputSchema);
                host.AssertValueOrNull(transposeInput);
                host.AssertValueOrNull(testType);

                // *** Binary format ***
                // int: number of added columns
                // for each added column
                //   int: id of output column name
                //   int: id of input column name
                int cinfo = ctx.Reader.ReadInt32();
                host.CheckDecode(cinfo > 0);

                var names = new string[cinfo];
                var infos = new ColInfo[cinfo];
                for (int i = 0; i < cinfo; i++)
                {
                    string dst = ctx.LoadNonEmptyString();
                    names[i] = dst;

                    // Note that in old files, the source name may be null indicating that
                    // the source column has the same name as the added column.
                    string tmp = ctx.LoadStringOrNull();
                    string src = tmp ?? dst;
                    host.CheckDecode(!string.IsNullOrEmpty(src));

                    int colSrc;
                    if (!inputSchema.TryGetColumnIndex(src, out colSrc))
                        throw host.Except("Source column '{0}' is required but not found", src);
                    var type = inputSchema[colSrc].Type;
                    if (testType != null)
                    {
                        string reason = testType(type);
                        if (reason != null)
                            throw host.Except(InvalidTypeErrorFormat, src, type, reason);
                    }
                    var slotType = transposeInput?.GetSlotType(i);
                    infos[i] = new ColInfo(dst, colSrc, type, slotType as VectorType);
                }

                return new Bindings(parent, infos, inputSchema, false, names);
            }

            public void Save(ModelSaveContext ctx)
            {
                Contracts.AssertValue(ctx);

                // *** Binary format ***
                // int: number of added columns
                // for each added column
                //   int: id of output column name
                //   int: id of input column name
                ctx.Writer.Write(Infos.Length);
                foreach (var info in Infos)
                {
                    ctx.SaveNonEmptyString(info.Name);
                    ctx.SaveNonEmptyString(Input[info.Source].Name);
                }
            }

            public Func<int, bool> GetDependencies(Func<int, bool> predicate)
            {
                Contracts.AssertValue(predicate);

                var active = new bool[Input.Count];
                for (int col = 0; col < ColumnCount; col++)
                {
                    if (!predicate(col))
                        continue;

                    bool isSrc;
                    int index = MapColumnIndex(out isSrc, col);
                    if (isSrc)
                        active[index] = true;
                    else
                        _parent.ActivateSourceColumns(index, active);
                }

                return col => 0 <= col && col < active.Length && active[col];
            }

            // The methods below here delegate to the parent transform.

            protected override ColumnType GetColumnTypeCore(int iinfo)
            {
                return _parent.GetColumnTypeCore(iinfo);
            }

            protected override IEnumerable<KeyValuePair<string, ColumnType>> GetMetadataTypesCore(int iinfo)
            {
                return _parent.Metadata.GetMetadataTypes(iinfo);
            }

            protected override ColumnType GetMetadataTypeCore(string kind, int iinfo)
            {
                return _parent.Metadata.GetMetadataTypeOrNull(kind, iinfo);
            }

            protected override void GetMetadataCore<TValue>(string kind, int iinfo, ref TValue value)
            {
                _parent.Metadata.GetMetadata<TValue>(_parent.Host, kind, iinfo, ref value);
            }
        }

        // This is used to simply communicate information from a constructor to a bindings constructor.
        private sealed class ColumnTmp : OneToOneColumn
        {
        }

        private readonly Bindings _bindings;

        // The ColInfos are exposed to sub-classes. They should be considered readonly.
        protected readonly ColInfo[] Infos;
        // The _input as a transposed data view, non-null iff _input is a transposed data view.
        private protected readonly ITransposeDataView InputTranspose;
        // The InputTranspose transpose schema, null iff InputTranspose is null.

        bool ICanSavePfa.CanSavePfa => CanSavePfaCore;

        private protected virtual bool CanSavePfaCore => false;

        bool ICanSaveOnnx.CanSaveOnnx(OnnxContext ctx) => CanSaveOnnxCore;

        private protected virtual bool CanSaveOnnxCore => false;

        [BestFriend]
        private protected OneToOneTransformBase(IHostEnvironment env, string name, OneToOneColumn[] column,
            IDataView input, Func<ColumnType, string> testType)
            : base(env, name, input)
        {
            Host.CheckUserArg(Utils.Size(column) > 0, nameof(column));
            Host.CheckValueOrNull(testType);
            InputTranspose = Source as ITransposeDataView;

            _bindings = Bindings.Create(this, column, Source.Schema, InputTranspose, testType);
            Infos = _bindings.Infos;
            Metadata = new MetadataDispatcher(Infos.Length);
        }

        [BestFriend]
        private protected OneToOneTransformBase(IHost host, OneToOneColumn[] column,
            IDataView input, Func<ColumnType, string> testType)
            : base(host, input)
        {
            Host.CheckUserArg(Utils.Size(column) > 0, nameof(column));
            Host.CheckValueOrNull(testType);
            InputTranspose = Source as ITransposeDataView;

            _bindings = Bindings.Create(this, column, Source.Schema, InputTranspose, testType);
            Infos = _bindings.Infos;
            Metadata = new MetadataDispatcher(Infos.Length);
        }

        [BestFriend]
        private protected OneToOneTransformBase(IHost host, ModelLoadContext ctx,
            IDataView input, Func<ColumnType, string> testType)
            : base(host, input)
        {
            Host.CheckValue(ctx, nameof(ctx));
            Host.CheckValueOrNull(testType);
            InputTranspose = Source as ITransposeDataView;

            _bindings = Bindings.Create(this, ctx, Source.Schema, InputTranspose, testType);
            Infos = _bindings.Infos;
            Metadata = new MetadataDispatcher(Infos.Length);
        }

        /// <summary>
        /// Re-applying constructor.
        /// </summary>
        [BestFriend]
        private protected OneToOneTransformBase(IHostEnvironment env, string name, OneToOneTransformBase transform,
            IDataView newInput, Func<ColumnType, string> checkType)
            : base(env, name, newInput)
        {
            Host.CheckValueOrNull(checkType);
            InputTranspose = Source as ITransposeDataView;

            OneToOneColumn[] map = transform.Infos
                .Select(x => new ColumnTmp
                {
                    Name = x.Name,
                    Source = transform.Source.Schema[x.Source].Name,
                })
                .ToArray();

            _bindings = Bindings.Create(this, map, newInput.Schema, InputTranspose, checkType);
            Infos = _bindings.Infos;
            Metadata = new MetadataDispatcher(Infos.Length);
        }

        [BestFriend]
        private protected MetadataDispatcher Metadata { get; }

        [BestFriend]
        private protected void SaveBase(ModelSaveContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            _bindings.Save(ctx);
        }

        void ISaveAsPfa.SaveAsPfa(BoundPfaContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            Host.Assert(((ICanSavePfa)this).CanSavePfa);

            var toHide = new List<string>();
            var toDeclare = new List<KeyValuePair<string, JToken>>();

            for (int iinfo = 0; iinfo < Infos.Length; ++iinfo)
            {
                var info = Infos[iinfo];
                var srcName = Source.Schema[info.Source].Name;
                string srcToken = ctx.TokenOrNullForName(srcName);
                if (srcToken == null)
                {
                    toHide.Add(info.Name);
                    continue;
                }
                var result = SaveAsPfaCore(ctx, iinfo, info, srcToken);
                if (result == null)
                {
                    toHide.Add(info.Name);
                    continue;
                }
                toDeclare.Add(new KeyValuePair<string, JToken>(info.Name, result));
            }
            ctx.Hide(toHide.ToArray());
            ctx.DeclareVar(toDeclare.ToArray());
        }

        void ISaveAsOnnx.SaveAsOnnx(OnnxContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            Host.Assert(((ICanSaveOnnx)this).CanSaveOnnx(ctx));

            for (int iinfo = 0; iinfo < Infos.Length; ++iinfo)
            {
                ColInfo info = Infos[iinfo];
                string inputColumnName = Source.Schema[info.Source].Name;
                if (!ctx.ContainsColumn(inputColumnName))
                {
                    ctx.RemoveColumn(info.Name, false);
                    continue;
                }

                if (!SaveAsOnnxCore(ctx, iinfo, info, ctx.GetVariableName(inputColumnName),
                    ctx.AddIntermediateVariable(OutputSchema[_bindings.MapIinfoToCol(iinfo)].Type, info.Name)))
                {
                    ctx.RemoveColumn(info.Name, true);
                }
            }
        }

        /// <summary>
        /// Called by <see cref="ISaveAsPfa.SaveAsPfa"/>. Should be implemented by subclasses that return
        /// <c>true</c> from <see cref="ICanSavePfa.CanSavePfa"/>. Will be called
        /// </summary>
        /// <param name="ctx">The context. Can be used to declare cells, access other information,
        /// and whatnot. This method should not actually, however, declare the variable corresponding
        /// to the output column. The calling method will do that.</param>
        /// <param name="iinfo">The index of the output column whose PFA is being composed</param>
        /// <param name="info">The column info</param>
        /// <param name="srcToken">The token in the PFA corresponding to the source col</param>
        /// <returns>Shuold return the declaration corresponding to the value of this column. Will
        /// return <c>null</c> in the event that we do not know how to express this column as PFA</returns>
        [BestFriend]
        private protected virtual JToken SaveAsPfaCore(BoundPfaContext ctx, int iinfo, ColInfo info, JToken srcToken)
        {
            Host.AssertValue(ctx);
            Host.Assert(0 <= iinfo && iinfo < _bindings.InfoCount);
            Host.Assert(Infos[iinfo] == info);
            Host.AssertValue(srcToken);
            Host.Assert(((ICanSavePfa)this).CanSavePfa);
            return null;
        }

        [BestFriend]
        private protected virtual bool SaveAsOnnxCore(OnnxContext ctx, int iinfo, ColInfo info, string srcVariableName,
            string dstVariableName) => false;

        public sealed override Schema OutputSchema => _bindings.AsSchema;

        VectorType ITransposeDataView.GetSlotType(int col) => _bindings.GetSlotType(col);

        /// <summary>
        /// Return the (destination) column index for the indicated added column.
        /// </summary>
        protected int ColumnIndex(int iinfo)
        {
            Host.Assert(0 <= iinfo && iinfo < Infos.Length);
            return _bindings.MapIinfoToCol(iinfo);
        }

        protected abstract ColumnType GetColumnTypeCore(int iinfo);

        protected virtual VectorType GetSlotTypeCore(int iinfo)
        {
            Host.Assert(0 <= iinfo && iinfo < Infos.Length);
            // By default, none of the added columns are transposable.
            return null;
        }

        /// <summary>
        /// Activates the source column.
        /// Override when you don't need the source column to generate the value for this column or when you need
        /// other auxiliary source columns that iinfo destination column depends on.
        /// </summary>
        protected virtual void ActivateSourceColumns(int iinfo, bool[] active)
        {
            Host.Assert(0 <= iinfo && iinfo < Infos.Length);
            active[Infos[iinfo].Source] = true;
        }

        /// <summary>
        /// Sub-classes implement this to provide, for a cursor, a getter delegate and optional disposer.
        /// If no action is needed when the cursor is Disposed, the override should set disposer to null,
        /// otherwise it should be set to a delegate to be invoked by the cursor's Dispose method. It's best
        /// for this action to be idempotent - calling it multiple times should be equivalent to calling it once.
        /// </summary>
        protected abstract Delegate GetGetterCore(IChannel ch, Row input, int iinfo, out Action disposer);

        protected ValueGetter<T> GetSrcGetter<T>(Row input, int iinfo)
        {
            Host.AssertValue(input);
            Host.Assert(0 <= iinfo && iinfo < Infos.Length);
            int src = Infos[iinfo].Source;
            Host.Assert(input.IsColumnActive(src));
            return input.GetGetter<T>(src);
        }

        protected Delegate GetSrcGetter(ColumnType typeDst, Row row, int iinfo)
        {
            Host.CheckValue(typeDst, nameof(typeDst));
            Host.CheckValue(row, nameof(row));

            Func<Row, int, ValueGetter<int>> del = GetSrcGetter<int>;
            var methodInfo = del.GetMethodInfo().GetGenericMethodDefinition().MakeGenericMethod(typeDst.RawType);
            return (Delegate)methodInfo.Invoke(this, new object[] { row, iinfo });
        }

        /// <summary>
        /// This produces either "true" or "null" according to whether <see cref="WantParallelCursors"/>
        /// returns true or false. Note that this will never return false. Any derived class
        /// must support (but not necessarily prefer) parallel cursors.
        /// </summary>
        protected sealed override bool? ShouldUseParallelCursors(Func<int, bool> predicate)
        {
            Host.AssertValue(predicate, "predicate");
            if (WantParallelCursors(predicate))
                return true;
            return null;
        }

        /// <summary>
        /// This should return true iff parallel cursors are advantageous. The default implementation
        /// returns true iff some columns added by this transform are active.
        /// </summary>
        protected virtual bool WantParallelCursors(Func<int, bool> predicate)
        {
            // Prefer parallel cursors iff some of our columns are active, otherwise, don't care.
            return _bindings.AnyNewColumnsActive(predicate);
        }

        protected override RowCursor GetRowCursorCore(IEnumerable<Schema.Column> columnsNeeded, Random rand = null)
        {
            Host.AssertValueOrNull(rand);

            Func<int, bool> needCol = c=> columnsNeeded == null ? false: columnsNeeded.Any(x => x.Index == c);

            var inputPred = _bindings.GetDependencies(needCol);
            var active = _bindings.GetActive(needCol);

            var inputCols = Source.Schema.Where(x => inputPred(x.Index));
            var input = Source.GetRowCursor(inputCols, rand);
            return new Cursor(Host, this, input, active);
        }

        public sealed override RowCursor[] GetRowCursorSet(IEnumerable<Schema.Column> columnsNeeded, int n, Random rand = null)
        {
            Host.CheckValueOrNull(rand);

            var predicate = RowCursorUtils.FromColumnsToPredicate(columnsNeeded, OutputSchema);

            var inputPred = _bindings.GetDependencies(predicate);
            var active = _bindings.GetActive(predicate);

            var inputCols = Source.Schema.Where(x => inputPred(x.Index));
            var inputs = Source.GetRowCursorSet(inputCols, n, rand);
            Host.AssertNonEmpty(inputs);

            if (inputs.Length == 1 && n > 1 && WantParallelCursors(predicate))
                inputs = DataViewUtils.CreateSplitCursors(Host, inputs[0], n);
            Host.AssertNonEmpty(inputs);

            var cursors = new RowCursor[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                cursors[i] = new Cursor(Host, this, inputs[i], active);
            return cursors;
        }

        /// <summary>
        /// Returns a standard exception for responding to an invalid call to <see cref="ITransposeDataView.GetSlotCursor"/>
        /// implementation in <see langword="this"/> on a column that is not transposable.
        /// </summary>
        protected Exception ExceptGetSlotCursor(int col)
        {
            Host.Assert(0 <= col && col < _bindings.ColumnCount);
            return Host.ExceptParam(nameof(col), "Bad call to GetSlotCursor on untransposable column '{0}'",
                OutputSchema[col].Name);
        }

        SlotCursor ITransposeDataView.GetSlotCursor(int col)
        {
            Host.CheckParam(0 <= col && col < _bindings.ColumnCount, nameof(col));

            bool isSrc;
            int index = _bindings.MapColumnIndex(out isSrc, col);
            if (isSrc)
            {
                if (InputTranspose != null)
                    return InputTranspose.GetSlotCursor(index);
                throw ExceptGetSlotCursor(col);
            }
            if (GetSlotTypeCore(index) == null)
                throw ExceptGetSlotCursor(col);
            return GetSlotCursorCore(index);
        }

        /// <summary>
        /// Implementors should note this only called if <see cref="GetSlotTypeCore"/>
        /// returns a non-null value for this <paramref name="iinfo"/>, so in principle
        /// it should always return a valid value, if called. This implementation throws,
        /// since the default implementation of <see cref="GetSlotTypeCore"/> will return
        /// null for all new columns, and so reaching this is only possible if there is a
        /// bug.
        /// </summary>
        [BestFriend]
        internal virtual SlotCursor GetSlotCursorCore(int iinfo)
        {
            Host.Assert(false);
            throw Host.ExceptNotImpl("Data view indicated it could transpose a column, but apparently it could not");
        }

        protected override int MapColumnIndex(out bool isSrc, int col)
        {
            return _bindings.MapColumnIndex(out isSrc, col);
        }

        protected override Func<int, bool> GetDependenciesCore(Func<int, bool> predicate)
        {
            return _bindings.GetDependencies(predicate);
        }

        protected override Delegate[] CreateGetters(Row input, Func<int, bool> active, out Action disposer)
        {
            Func<int, bool> activeInfos =
                iinfo =>
                {
                    int col = _bindings.MapIinfoToCol(iinfo);
                    return active(col);
                };

            var getters = new Delegate[_bindings.InfoCount];
            disposer = null;
            using (var ch = Host.Start("CreateGetters"))
            {
                for (int iinfo = 0; iinfo < _bindings.InfoCount; iinfo++)
                {
                    if (!activeInfos(iinfo))
                        continue;
                    Action disp;
                    getters[iinfo] = GetGetterCore(ch, input, iinfo, out disp);
                    disposer += disp;
                }
                return getters;
            }
        }

        private sealed class Cursor : SynchronizedCursorBase
        {
            private readonly Bindings _bindings;
            private readonly bool[] _active;

            private readonly Delegate[] _getters;
            private readonly Action _disposer;
            private bool _disposed;

            public Cursor(IChannelProvider provider, OneToOneTransformBase parent, RowCursor input, bool[] active)
                : base(provider, input)
            {
                Ch.AssertValue(parent);
                Ch.Assert(active == null || active.Length == parent._bindings.ColumnCount);

                _bindings = parent._bindings;
                _active = active;
                _getters = new Delegate[parent.Infos.Length];

                // Build the disposing delegate.
                Action masterDisposer = null;
                for (int iinfo = 0; iinfo < _getters.Length; iinfo++)
                {
                    if (!IsColumnActive(parent._bindings.MapIinfoToCol(iinfo)))
                        continue;
                    _getters[iinfo] = parent.GetGetterCore(Ch, Input, iinfo, out Action disposer);
                    if (disposer != null)
                        masterDisposer += disposer;
                }
                _disposer = masterDisposer;
            }

            protected override void Dispose(bool disposing)
            {
                if (_disposed)
                    return;
                if (disposing)
                {
                    _disposer?.Invoke();
                }
                _disposed = true;
                base.Dispose(disposing);
            }

            public override Schema Schema => _bindings.AsSchema;

            public override ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                Ch.Check(IsColumnActive(col));

                bool isSrc;
                int index = _bindings.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return Input.GetGetter<TValue>(index);

                Ch.Assert(_getters[index] != null);
                var fn = _getters[index] as ValueGetter<TValue>;
                if (fn == null)
                    throw Ch.Except("Invalid TValue in GetGetter: '{0}'", typeof(TValue));
                return fn;
            }

            public override bool IsColumnActive(int col)
            {
                Ch.Check(0 <= col && col < _bindings.ColumnCount);
                return _active == null || _active[col];
            }
        }

        protected static string TestIsText(ColumnType type)
        {
            if (type is TextType)
                return null;
            return "Expected Text type";
        }

        protected static string TestIsTextItem(ColumnType type)
        {
            if (type.GetItemType() is TextType)
                return null;
            return "Expected Text type";
        }

        protected static string TestIsTextVector(ColumnType type)
        {
            if (type is VectorType vectorType && vectorType.ItemType is TextType)
                return null;
            return "Expected vector of Text type";
        }

        protected static string TestIsFloatItem(ColumnType type)
        {
            if (type.GetItemType() == NumberType.Float)
                return null;
            return "Expected R4 or a vector of R4";
        }

        protected static string TestIsFloatVector(ColumnType type)
        {
            if (type is VectorType vectorType && vectorType.ItemType == NumberType.Float)
                return null;

            return "Expected Float vector";
        }

        protected static string TestIsKnownSizeFloatVector(ColumnType type)
        {
            if (type is VectorType vectorType
                && vectorType.IsKnownSize
                && vectorType.ItemType == NumberType.Float)
                return null;

            return "Expected Float vector of known size";
        }

        protected static string TestIsKey(ColumnType type)
        {
            if (type.GetItemType().GetKeyCount() > 0)
                return null;
            return "Expected Key type of known cardinality";
        }
    }
}
