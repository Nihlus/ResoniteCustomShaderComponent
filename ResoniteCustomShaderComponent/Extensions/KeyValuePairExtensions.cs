//
//  SPDX-FileName: KeyValuePairExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="KeyValuePair{TKey,TValue}"/> struct.
/// </summary>
public static class KeyValuePairExtensions
{
    /// <summary>
    /// Deconstructs the given key-value pair.
    /// </summary>
    /// <param name="kvp">The key-value pair.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
    {
        key = kvp.Key;
        value = kvp.Value;
    }
}
