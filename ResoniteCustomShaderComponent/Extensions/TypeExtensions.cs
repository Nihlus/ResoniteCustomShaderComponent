//
//  SPDX-FileName: TypeExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extensions for the <see cref="Type"/> class.
/// </summary>
public static class TypeExtensions
{
    /// <summary>
    /// Determines whether the type is a scalar numeric type.
    /// </summary>
    /// <param name="value">The type.</param>
    /// <returns>true if the type is a scalar numeric type; otherwise, false.</returns>
    public static bool IsScalar(this Type value) => value switch
    {
        _ when value == typeof(sbyte) => true,
        _ when value == typeof(byte) => true,
        _ when value == typeof(short) => true,
        _ when value == typeof(ushort) => true,
        _ when value == typeof(int) => true,
        _ when value == typeof(uint) => true,
        _ when value == typeof(long) => true,
        _ when value == typeof(ulong) => true,
        _ when value == typeof(float) => true,
        _ when value == typeof(double) => true,
        _ when value == typeof(decimal) => true,
        _ => false
    };
}
