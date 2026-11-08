// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Enumeration representing the type of escaping needed for the values.
    /// </summary>
    public enum PipDataFragmentEscaping : byte
    {
        /// <summary>
        /// Invalid, default value.
        /// </summary>
        Invalid = 0,

        /// <summary>
        /// No need for escaping, just concatenate the values.
        /// </summary>
        NoEscaping,

        /// <summary>
        /// The Standard C Runtime parsing semantics.
        /// </summary>
        /// <remarks>
        /// See http://msdn.microsoft.com/en-us/library/a1y7w461.aspx for semantics.
        /// </remarks>
        CRuntimeArgumentRules,

        /// <summary>
        /// Renders a single child fragment, then encodes its complete value as one C-runtime argument.
        /// Unlike CRuntimeArgumentRules, this applies after rendering nested data.
        /// </summary>
        EscapeArgument,

        /// <summary>
        /// Renders each child completely, escapes it as one argument, then joins the escaped values.
        /// Nested children retain their own rendering rules before whole-child escaping.
        /// </summary>
        EscapeEachArgument,

        ///// <summary>
        ///// Encoded as argument for Cmd.exe.
        ///// </summary>
        ///// <remarks>
        ///// This inserts the funny ^ escapes in the right place.
        ///// </remarks>
        // CmdExe,
    }
}
