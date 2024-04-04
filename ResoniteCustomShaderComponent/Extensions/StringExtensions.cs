//
//  SPDX-FileName: StringExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Globalization;
using System.Security.Cryptography;

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="string"/> class.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Determines if a string looks like a SHA-256 hash (a 64-character hexadecimal string).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>true if the value looks like a SHA-256 hash; otherwise, false.</returns>
    public static bool LooksLikeSHA256Hash(this string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                // valid hexadecimal digit
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is < 'a' or > 'f')
            {
                // not a hexadecimal digit
                return false;
            }
        }

        return true;
    }
}
