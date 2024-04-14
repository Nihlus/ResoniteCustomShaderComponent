//
//  SPDX-FileName: BlendModePropertyGroup.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using StrictEmit;
using Shader = UnityEngine.Shader;

namespace ResoniteCustomShaderComponent.TypeGeneration.Properties;

/// <summary>
/// Represents a group of virtual and real properties that control a shader's blend mode.
/// </summary>
public sealed class BlendModePropertyGroup : MaterialPropertyGroup
{
    private readonly NativeMaterialProperty _srcBlend;
    private readonly NativeMaterialProperty _dstBlend;
    private readonly NativeMaterialProperty? _srcBlendAdd;
    private readonly NativeMaterialProperty? _dstBlendAdd;

    private static readonly MethodInfo _updateBlendMode = typeof(MaterialProvider)
        .GetMethod("UpdateBlendMode", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo _setBlendModeKeywords = typeof(MaterialProvider)
        .GetMethod
        (
            "SetBlendModeKeywords",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(ShaderKeywords), typeof(Sync<BlendMode>)],
            []
        )!;

    /// <summary>
    /// Gets the virtual blend mode property.
    /// </summary>
    public ManagedMaterialProperty BlendMode { get; }

    /// <summary>
    /// Gets the virtual render queue property.
    /// </summary>
    public ManagedMaterialProperty RenderQueue { get; }

    /// <summary>
    /// Gets the Z-buffer write property (if present).
    /// </summary>
    public ManagedMaterialProperty? ZWrite { get; }

    /// <summary>
    /// Gets the corresponding native Z-buffer write property (if present).
    /// </summary>
    public NativeMaterialProperty? NativeZWrite { get; }

    /// <summary>
    /// Gets the managed property that controls sidedness.
    /// </summary>
    public ManagedMaterialProperty? Sidedness { get; }

    /// <summary>
    /// Gets the corresponding native culling property.
    /// </summary>
    public NativeMaterialProperty? NativeCull { get; }

    /// <summary>
    /// Gets the default blend mode.
    /// </summary>
    public BlendMode DefaultBlendMode { get; }

    /// <summary>
    /// Gets the default render queue.
    /// </summary>
    public int DefaultRenderQueue { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlendModePropertyGroup"/> class.
    /// </summary>
    /// <param name="shader">The shader that the group belongs to.</param>
    /// <param name="srcBlend">The source blend property.</param>
    /// <param name="dstBlend">The destination blend property.</param>
    /// <param name="srcBlendAdd">The additive source blend property.</param>
    /// <param name="dstBlendAdd">The additive destination blend property.</param>
    /// <param name="nativeZWrite">The native Z-buffer write property.</param>
    /// <param name="nativeCull">The native polygon culling property.</param>
    public BlendModePropertyGroup
    (
        Shader shader,
        NativeMaterialProperty srcBlend,
        NativeMaterialProperty dstBlend,
        NativeMaterialProperty? srcBlendAdd = null,
        NativeMaterialProperty? dstBlendAdd = null,
        NativeMaterialProperty? nativeZWrite = null,
        NativeMaterialProperty? nativeCull = null
    )
    {
        _srcBlend = srcBlend;
        _dstBlend = dstBlend;
        _srcBlendAdd = srcBlendAdd;
        _dstBlendAdd = dstBlendAdd;

        BlendMode = new ManagedMaterialProperty(nameof(BlendMode), typeof(BlendMode));
        RenderQueue = new ManagedMaterialProperty(nameof(RenderQueue), typeof(int));

        NativeZWrite = nativeZWrite;
        if (nativeZWrite is not null)
        {
            ZWrite = new ManagedMaterialProperty(nameof(ZWrite), typeof(ZWrite));
        }

        NativeCull = nativeCull;
        if (nativeCull is not null)
        {
            Sidedness = new ManagedMaterialProperty(nameof(Sidedness), typeof(Sidedness));
        }

        DefaultBlendMode = shader.GetDefaultBlendMode();
        DefaultRenderQueue = shader.renderQueue;
    }

    /// <inheritdoc />
    public override IEnumerable<ManagedMaterialProperty> GetManagedProperties()
    {
        yield return BlendMode;
        yield return RenderQueue;

        if (ZWrite is not null)
        {
            yield return ZWrite;
        }

        if (Sidedness is not null)
        {
            yield return Sidedness;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<NativeMaterialProperty> GetNativeProperties()
    {
        yield return _srcBlend;
        yield return _dstBlend;

        if (_srcBlendAdd is not null)
        {
            yield return _srcBlendAdd;
        }

        if (_dstBlendAdd is not null)
        {
            yield return _dstBlendAdd;
        }

        if (NativeZWrite is not null)
        {
            yield return NativeZWrite;
        }

        if (NativeCull is not null)
        {
            yield return NativeCull;
        }
    }

    /// <inheritdoc />
    public override void EmitInitializeSyncMemberDefaults(ILGenerator il)
    {
        if (BlendMode.Field is null || RenderQueue.Field is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(BlendMode.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantInt((int)DefaultBlendMode);

        // stack:
        //   ISyncMember
        //   BlendMode
        il.EmitSetProperty
        (
            BlendMode.Field.FieldType,
            "Value"
        );

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(RenderQueue.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantInt(DefaultRenderQueue);

        // stack:
        //   ISyncMember
        //   BlendMode
        il.EmitSetProperty
        (
            RenderQueue.Field.FieldType,
            "Value"
        );

        if (ZWrite is not null)
        {
            if (ZWrite.Field is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(0);

            // stack:
            //   this
            il.EmitLoadField(ZWrite.Field);

            // stack:
            //   ISyncMember
            il.EmitConstantInt((int)FrooxEngine.ZWrite.Auto);

            // stack:
            //   ISyncMember
            //   BlendMode
            il.EmitSetProperty
            (
                ZWrite.Field.FieldType,
                "Value"
            );
        }

        if (Sidedness is not null)
        {
            if (Sidedness.Field is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(0);

            // stack:
            //   this
            il.EmitLoadField(Sidedness.Field);

            // stack:
            //   ISyncMember
            il.EmitConstantInt((int)FrooxEngine.Sidedness.Auto);

            // stack:
            //   ISyncMember
            //   BlendMode
            il.EmitSetProperty
            (
                Sidedness.Field.FieldType,
                "Value"
            );
        }
    }

    /// <inheritdoc />
    public override void EmitUpdateKeywords(ILGenerator il)
    {
        if (BlendMode.Field is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadArgument(1);

        // stack:
        //   this
        //   ShaderKeywords
        il.EmitLoadArgument(0);
        il.EmitLoadField(BlendMode.Field);

        // stack:
        //   this
        //   ShaderKeywords
        //   Sync<BlendMode>
        il.EmitCallVirtual(_setBlendModeKeywords);
    }

    /// <inheritdoc />
    public override void EmitUpdateMaterial(ILGenerator il)
    {
        if (BlendMode.Field is null || RenderQueue.Field is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadArgument(1);

        // stack:
        //   this
        //   Material
        il.EmitLoadArgument(0);
        il.EmitLoadField(BlendMode.Field);

        // stack:
        //   this
        //   Material
        //   ISyncMember
        if (ZWrite is null)
        {
            il.EmitLoadNull();
        }
        else
        {
            if (ZWrite.Field is null)
            {
                throw new InvalidOperationException();
            }

            il.EmitLoadArgument(0);
            il.EmitLoadField(ZWrite.Field);
        }

        // stack:
        //   this
        //   Material
        //   ISyncMember
        //   ISyncMember?
        il.EmitLoadArgument(0);
        il.EmitLoadField(RenderQueue.Field);

        // stack:
        //   this
        //   Material
        //   ISyncMember
        //   ISyncMember?
        //   ISyncMember
        il.EmitCallVirtual(_updateBlendMode);

        if (Sidedness is not null && NativeCull is not null)
        {
            if (Sidedness.Field is null || NativeCull.PropertyNameField is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(1);

            // stack:
            //   Material
            il.EmitLoadStaticField(NativeCull.PropertyNameField);
            il.EmitCallDirect(MaterialPropertyMapper.MaterialPropertyConversion);

            // stack:
            //   Material
            //   int
            il.EmitLoadArgument(0);
            il.EmitLoadField(Sidedness.Field);

            // stack:
            //   Material
            //   int
            //   Sync<Sidedness>
            il.EmitLoadArgument(0);
            il.EmitLoadField(BlendMode.Field);

            // stack:
            //   Material
            //   int
            //   Sync<Sidedness>
            //   Sync<BlendMode>
            il.EmitCallDirect(typeof(MaterialExtensions).GetMethod(nameof(MaterialExtensions.UpdateSidedness))!);
        }
    }
}
