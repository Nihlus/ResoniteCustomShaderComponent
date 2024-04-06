//
//  SPDX-FileName: ScaleTranslationPropertyGroup.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using StrictEmit;
using UnityEngine;

namespace ResoniteCustomShaderComponent.TypeGeneration.Properties;

/// <summary>
/// Represents a split scale/translation property group that maps to an underlying <see cref="Vector4"/>.
/// </summary>
public sealed class ScaleTranslationPropertyGroup : MaterialPropertyGroup
{
    private static readonly MethodInfo _updateST = typeof(FrooxEngine.Material)
        .GetMethod(nameof(FrooxEngine.Material.UpdateST))!;

    /// <summary>
    /// Gets the virtual scale property.
    /// </summary>
    public ManagedMaterialProperty Scale { get; }

    /// <summary>
    /// Gets the virtual translation property.
    /// </summary>
    public ManagedMaterialProperty Translation { get; }

    /// <summary>
    /// Gets the target native property.
    /// </summary>
    public NativeMaterialProperty Native { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ScaleTranslationPropertyGroup"/> class.
    /// </summary>
    /// <param name="scaleName">The name of the managed scale property.</param>
    /// <param name="translationName">The mane of the managed translation property.</param>
    /// <param name="native">The native property.</param>
    public ScaleTranslationPropertyGroup(string scaleName, string translationName, NativeMaterialProperty native)
    {
        this.Scale = new ManagedMaterialProperty(scaleName, typeof(float2));
        this.Translation = new ManagedMaterialProperty(translationName, typeof(float2));
        this.Native = native;
    }

    /// <inheritdoc />
    public override IEnumerable<ManagedMaterialProperty> GetManagedProperties()
    {
        yield return this.Scale;
        yield return this.Translation;
    }

    /// <inheritdoc />
    public override IEnumerable<NativeMaterialProperty> GetNativeProperties()
    {
        yield return this.Native;
    }

    /// <inheritdoc />
    public override void EmitInitializeSyncMemberDefaults(ILGenerator il)
    {
        if (this.Scale.Field is null || this.Translation.Field is null)
        {
            throw new InvalidOperationException();
        }

        var float2Constructor = typeof(float2).GetConstructor([typeof(float), typeof(float)])!;
        var defaultVector = this.Native.DefaultVector ?? new Vector4(1, 1, 0, 0);

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(this.Scale.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantFloat(defaultVector[0]);

        // stack:
        //   ISyncMember
        //   float
        il.EmitConstantFloat(defaultVector[1]);

        // stack:
        //   ISyncMember
        //   float
        //   float
        il.EmitNewObject(float2Constructor);

        // stack:
        //   ISyncMember
        //   float2
        il.EmitSetProperty
        (
            this.Scale.Field.FieldType,
            "Value"
        );

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(this.Translation.Field);

        // stack:
        //   ISyncMember
        il.EmitConstantFloat(defaultVector[0]);

        // stack:
        //   ISyncMember
        //   float
        il.EmitConstantFloat(defaultVector[1]);

        // stack:
        //   ISyncMember
        //   float
        //   float
        il.EmitNewObject(float2Constructor);

        // stack:
        //   ISyncMember
        //   float2
        il.EmitSetProperty
        (
            this.Translation.Field.FieldType,
            "Value"
        );
    }

    /// <inheritdoc />
    public override void EmitUpdateMaterial(ILGenerator il)
    {
        if (this.Scale.Field is null || this.Translation.Field is null || this.Native.PropertyNameField is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(1);

        // stack:
        //   Material
        il.EmitLoadStaticField(this.Native.PropertyNameField);

        // stack:
        //   Material
        //   MaterialProperty
        il.EmitCallDirect(MaterialPropertyMapper.MaterialPropertyConversion);

        // stack:
        //   Material
        //   int
        il.EmitLoadArgument(0);

        // stack:
        //   Material
        //   int
        //   this
        il.EmitLoadField(this.Scale.Field);

        // stack:
        //   Material
        //   int
        //   ISyncMember
        il.EmitLoadArgument(0);

        // stack:
        //   Material
        //   int
        //   ISyncMember
        //   this
        il.EmitLoadField(this.Translation.Field);

        // stack:
        //   Material
        //   int
        //   ISyncMember
        //   ISyncMember
        il.EmitCallVirtual(_updateST);
    }
}
