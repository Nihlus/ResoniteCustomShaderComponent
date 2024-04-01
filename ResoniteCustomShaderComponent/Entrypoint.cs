//
//  SPDX-FileName: Entrypoint.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.ComponentModel;
using System.Diagnostics;

[module: Description("FROOXENGINE_WEAVED")]

namespace Doorstop;

/// <summary>
/// Holds Doorstop's entrypoint code.
/// </summary>
public static class Entrypoint
{
    /// <summary>
    /// Runs Doorstop's entrypoint code.
    /// </summary>
    public static void Start()
    {
        Debugger.Break();
    }
}
