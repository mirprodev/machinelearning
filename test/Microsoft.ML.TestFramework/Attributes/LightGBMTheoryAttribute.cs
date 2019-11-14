﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.ML.TestFrameworkCommon.Attributes;

namespace Microsoft.ML.TestFramework.Attributes
{
    /// <summary>
    /// A theory for tests requiring LightGBM.
    /// </summary>
    public sealed class LightGBMTheoryAttribute : EnvironmentSpecificTheoryAttribute
    {
        public LightGBMTheoryAttribute() : base("LightGBM is 64-bit only")
        {
        }

        /// <inheritdoc />
        protected override bool IsEnvironmentSupported()
        {
            return Environment.Is64BitProcess;
        }
    }
}
