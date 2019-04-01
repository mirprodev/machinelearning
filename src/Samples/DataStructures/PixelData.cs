﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Data;

namespace Samples.DataStructures
{
    public class PixelData
    {
        public float Label;

        [ColumnName("PixelValues")]
        [VectorType(64)]
        public float[] PixelValues;
    }
}
