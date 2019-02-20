﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;

namespace Microsoft.ML.Auto
{
    internal sealed class OptimizingMetricInfo
    {
        public bool IsMaximizing { get; }

        private static RegressionMetric[] _minimizingRegressionMetrics = new RegressionMetric[]
        {
            RegressionMetric.L1,
            RegressionMetric.L2,
            RegressionMetric.Rms
        };

        private static BinaryClassificationMetric[] _minimizingBinaryMetrics = new BinaryClassificationMetric[]
        {
        };

        private static MulticlassClassificationMetric[] _minimizingMulticlassMetrics = new MulticlassClassificationMetric[]
        {
            MulticlassClassificationMetric.LogLoss,
        };

        public OptimizingMetricInfo(RegressionMetric regressionMetric)
        {
            IsMaximizing = !_minimizingRegressionMetrics.Contains(regressionMetric);
        }

        public OptimizingMetricInfo(BinaryClassificationMetric binaryMetric)
        {
            IsMaximizing = !_minimizingBinaryMetrics.Contains(binaryMetric);
        }

        public OptimizingMetricInfo(MulticlassClassificationMetric multiMetric)
        {
            IsMaximizing = !_minimizingMulticlassMetrics.Contains(multiMetric);
        }
    }
}
