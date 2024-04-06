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

        this.BlendMode = new ManagedMaterialProperty(nameof(this.BlendMode), typeof(BlendMode));
        this.RenderQueue = new ManagedMaterialProperty(nameof(this.RenderQueue), typeof(int));

        this.NativeZWrite = nativeZWrite;
        if (nativeZWrite is not null)
        {
            this.ZWrite = new ManagedMaterialProperty(nameof(this.ZWrite), typeof(ZWrite));
        }

        this.NativeCull = nativeCull;
        if (nativeCull is not null)
        {
            this.Sidedness = new ManagedMaterialProperty(nameof(this.Sidedness), typeof(Sidedness));
        }

        this.DefaultBlendMode = shader.GetDefaultBlendMode();
        this.DefaultRenderQueue = shader.renderQueue;
    }

    /// <inheritdoc />
    public override IEnumerable<ManagedMaterialProperty> GetManagedProperties()
    {
        yield return this.BlendMode;
        yield return this.RenderQueue;

        if (this.ZWrite is not null)
        {
            yield return this.ZWrite;
        }

        if (this.Sidedness is not null)
        {
            yield return this.Sidedness;
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

        if (this.NativeZWrite is not null)
        {
            yield return this.NativeZWrite;
        }

        if (this.NativeCull is not null)
        {
            yield return this.NativeCull;
        }
    }

    /// <inheritdoc />
    public override void EmitInitializeSyncMemberDefaults(ILGenerator il)
    {
        if (this.BlendMode.Field is null || this.RenderQueue.Field is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(this.BlendMode.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantInt((int)this.DefaultBlendMode);

        // stack:
        //   ISyncMember
        //   BlendMode
        il.EmitSetProperty
        (
            this.BlendMode.Field.FieldType,
            "Value"
        );

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(this.RenderQueue.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantInt(this.DefaultRenderQueue);

        // stack:
        //   ISyncMember
        //   BlendMode
        il.EmitSetProperty
        (
            this.RenderQueue.Field.FieldType,
            "Value"
        );

        if (this.ZWrite is not null)
        {
            if (this.ZWrite.Field is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(0);

            // stack:
            //   this
            il.EmitLoadField(this.ZWrite.Field);

            // stack:
            //   ISyncMember
            il.EmitConstantInt((int)FrooxEngine.ZWrite.Auto);

            // stack:
            //   ISyncMember
            //   BlendMode
            il.EmitSetProperty
            (
                this.ZWrite.Field.FieldType,
                "Value"
            );
        }

        if (this.Sidedness is not null)
        {
            if (this.Sidedness.Field is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(0);

            // stack:
            //   this
            il.EmitLoadField(this.Sidedness.Field);

            // stack:
            //   ISyncMember
            il.EmitConstantInt((int)FrooxEngine.Sidedness.Auto);

            // stack:
            //   ISyncMember
            //   BlendMode
            il.EmitSetProperty
            (
                this.Sidedness.Field.FieldType,
                "Value"
            );
        }
    }

    /// <inheritdoc />
    public override void EmitUpdateMaterial(ILGenerator il)
    {
        if (this.BlendMode.Field is null || this.RenderQueue.Field is null)
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
        il.EmitLoadField(this.BlendMode.Field);

        // stack:
        //   this
        //   Material
        //   ISyncMember
        if (this.ZWrite is null)
        {
            il.EmitLoadNull();
        }
        else
        {
            if (this.ZWrite.Field is null)
            {
                throw new InvalidOperationException();
            }

            il.EmitLoadArgument(0);
            il.EmitLoadField(this.ZWrite.Field);
        }

        // stack:
        //   this
        //   Material
        //   ISyncMember
        //   ISyncMember?
        il.EmitLoadArgument(0);
        il.EmitLoadField(this.RenderQueue.Field);

        // stack:
        //   this
        //   Material
        //   ISyncMember
        //   ISyncMember?
        //   ISyncMember
        il.EmitCallVirtual(_updateBlendMode);

        if (this.Sidedness is not null && this.NativeCull is not null)
        {
            if (this.Sidedness.Field is null || this.NativeCull.PropertyNameField is null)
            {
                throw new InvalidOperationException();
            }

            // stack:
            //   <empty>
            il.EmitLoadArgument(1);

            // stack:
            //   Material
            il.EmitLoadStaticField(this.NativeCull.PropertyNameField);
            il.EmitCallDirect(MaterialPropertyMapper.MaterialPropertyConversion);

            // stack:
            //   Material
            //   int
            il.EmitLoadArgument(0);
            il.EmitLoadField(this.Sidedness.Field);

            // stack:
            //   Material
            //   int
            //   Sync<Sidedness>
            il.EmitLoadArgument(0);
            il.EmitLoadField(this.BlendMode.Field);

            // stack:
            //   Material
            //   int
            //   Sync<Sidedness>
            //   Sync<BlendMode>
            il.EmitCallDirect(typeof(MaterialExtensions).GetMethod(nameof(MaterialExtensions.UpdateSidedness))!);
        }
    }
}
