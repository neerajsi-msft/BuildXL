// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace GraphRenderer
{
    internal static class Program
    {
        public static int Main(string[] args) => Renderer.Run(args, Console.Out, Console.Error);
    }
}