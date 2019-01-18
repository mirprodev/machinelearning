// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.Core.Data;
using Microsoft.ML.Data;

namespace Microsoft.ML.Auto
{
    /// <summary>
    /// A runnable pipeline. Contains a learner and set of transforms,
    /// along with a RunSummary if it has already been exectued.
    /// </summary>
    internal class InferredPipeline
    {
        private readonly MLContext _context;
        public readonly IList<SuggestedTransform> Transforms;
        public readonly SuggestedTrainer Trainer;

        public InferredPipeline(IEnumerable<SuggestedTransform> transforms,
            SuggestedTrainer trainer,
            MLContext context = null,
            bool autoNormalize = true)
        {
            Transforms = transforms.Select(t => t.Clone()).ToList();
            Trainer = trainer.Clone();
            _context = context ?? new MLContext();

            if(autoNormalize)
            {
                AddNormalizationTransforms();
            }
        }
        
        public override string ToString() => $"{Trainer}+{string.Join("+", Transforms.Select(t => t.ToString()))}";

        public override bool Equals(object obj)
        {
            var pipeline = obj as InferredPipeline;
            if(pipeline == null)
            {
                return false;
            }
            return pipeline.ToString() == this.ToString();
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public Pipeline ToPipeline()
        {
            var pipelineElements = new List<PipelineNode>();
            foreach(var transform in Transforms)
            {
                pipelineElements.Add(transform.PipelineNode);
            }
            pipelineElements.Add(Trainer.ToPipelineNode());
            return new Pipeline(pipelineElements.ToArray());
        }

        public static InferredPipeline FromPipeline(Pipeline pipeline)
        {
            var context = new MLContext();

            var transforms = new List<SuggestedTransform>();
            SuggestedTrainer trainer = null;

            foreach(var pipelineNode in pipeline.Elements)
            {
                if(pipelineNode.ElementType == PipelineNodeType.Trainer)
                {
                    var trainerName = (TrainerName)Enum.Parse(typeof(TrainerName), pipelineNode.Name);
                    var trainerExtension = TrainerExtensionCatalog.GetTrainerExtension(trainerName);
                    var stringParamVals = pipelineNode.Properties.Select(prop => new StringParameterValue(prop.Key, prop.Value.ToString()));
                    var hyperParamSet = new ParameterSet(stringParamVals);
                    trainer = new SuggestedTrainer(context, trainerExtension, hyperParamSet);
                }
                else if (pipelineNode.ElementType == PipelineNodeType.Transform)
                {
                    var estimatorName = (EstimatorName)Enum.Parse(typeof(EstimatorName), pipelineNode.Name);
                    var estimatorExtension = EstimatorExtensionCatalog.GetExtension(estimatorName);
                    var estimator = estimatorExtension.CreateInstance(new MLContext(), pipelineNode);
                    var transform = new SuggestedTransform(pipelineNode, estimator);
                    transforms.Add(transform);
                }
            }

            return new InferredPipeline(transforms, trainer, null, false);
        }

        public IEstimator<ITransformer> ToEstimator()
        {
            IEstimator<ITransformer> pipeline = new EstimatorChain<ITransformer>();

            // append each transformer to the pipeline
            foreach (var transform in Transforms)
            {
                if(transform.Estimator != null)
                {
                    pipeline = pipeline.Append(transform.Estimator);
                }
            }

            // get learner
            var learner = Trainer.BuildTrainer(_context);

            // append learner to pipeline
            pipeline = pipeline.Append(learner);

            return pipeline;
        }

        public ITransformer TrainTransformer(IDataView trainData)
        {
            var estimator = ToEstimator();
            return estimator.Fit(trainData);
        }

        private void AddNormalizationTransforms()
        {
            // get learner
            var learner = Trainer.BuildTrainer(_context);

            // only add normalization if learner needs it
            if (!learner.Info.NeedNormalization)
            {
                return;
            }

            var transform = NormalizingExtension.CreateSuggestedTransform(_context, DefaultColumnNames.Features, DefaultColumnNames.Features);
            Transforms.Add(transform);
        }
    }
}