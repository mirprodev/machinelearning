﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.DataView;
using Microsoft.ML.Data;

namespace Microsoft.ML.Auto
{
    internal class TransformPostTrainerInference
    {
        public static IEnumerable<SuggestedTransform> InferTransforms(MLContext context, TaskKind task, (string, DataViewType, ColumnPurpose, ColumnDimensions)[] columns)
        {
            var suggestedTransforms = new List<SuggestedTransform>();
            suggestedTransforms.AddRange(InferLabelTransforms(context, task, columns));
            return suggestedTransforms;
        }

        private static IEnumerable<SuggestedTransform> InferLabelTransforms(MLContext context, TaskKind task, 
            (string name, DataViewType type, ColumnPurpose purpose, ColumnDimensions dimensions)[] columns)
        {
            var inferredTransforms = new List<SuggestedTransform>();

            if (task != TaskKind.MulticlassClassification)
            {
                return inferredTransforms;
            }

            // If label column type wasn't originally key type,
            // convert predicted label column back from key to value.
            // (Non-key label column was converted to key, b/c multiclass trainers only 
            // accept label columns that are key type)
            var labelColumn = columns.First(c => c.purpose == ColumnPurpose.Label);
            if (!labelColumn.type.IsKey())
            {
                inferredTransforms.Add(KeyToValueMappingExtension.CreateSuggestedTransform(context, DefaultColumnNames.PredictedLabel, DefaultColumnNames.PredictedLabel));
            }

            return inferredTransforms;
        }
    }
}