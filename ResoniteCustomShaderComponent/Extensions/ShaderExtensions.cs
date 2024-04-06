//
//  SPDX-FileName: ShaderExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using UnityEngine.Rendering;
using BlendMode = FrooxEngine.BlendMode;

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="UnityEngine.Shader"/> class.
/// </summary>
public static class ShaderExtensions
{
    /// <summary>
    /// Gets the default blend mode for the given shader.
    /// </summary>
    /// <param name="shader">The shader.</param>
    /// <returns>The default blend mode.</returns>
    public static BlendMode GetDefaultBlendMode(this UnityEngine.Shader shader)
    {
        var renderType = "Opaque";
        for (var i = 0; i < shader.passCount; i++)
        {
            var tag = shader.FindPassTagValue(i, new ShaderTagId("RenderType"));
            if (!string.IsNullOrWhiteSpace(tag.name))
            {
                renderType = tag.name;
            }
        }

        switch (renderType)
        {
            case "Opaque":
            {
                return BlendMode.Opaque;
            }
            case "TransparentCutout":
            {
                return BlendMode.Cutout;
            }
            case "Transparent":
            {
                var srcBlendIndex = shader.FindPropertyIndex("_SrcBlend");
                var dstBlendIndex = shader.FindPropertyIndex("_DstBlend");

                var srcBlendDefault = shader.GetPropertyDefaultFloatValue(srcBlendIndex);
                var dstBlendDefault = shader.GetPropertyDefaultFloatValue(dstBlendIndex);

                return (srcBlendDefault, dstBlendDefault) switch
                {
                    (5.0f, 10.0f) => BlendMode.Alpha,
                    (1.0f, 10.0f) => BlendMode.Transparent,
                    (1.0f, 1.0f) => BlendMode.Additive,
                    (2.0f, 0.0f) => BlendMode.Multiply,
                    _ => BlendMode.Opaque // fallback
                };
            }
            default:
            {
                return BlendMode.Opaque;
            }
        }
    }
}
