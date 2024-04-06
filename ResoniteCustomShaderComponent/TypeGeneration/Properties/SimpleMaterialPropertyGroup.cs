//
//  SPDX-FileName: SimpleMaterialPropertyGroup.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection.Emit;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using StrictEmit;
using UnityEngine.Rendering;

namespace ResoniteCustomShaderComponent.TypeGeneration.Properties;

/// <summary>
/// Represents a simple, 1:1 native-to-managed property. The managed property may have a different type than the native
/// property.
/// </summary>
public sealed class SimpleMaterialPropertyGroup : MaterialPropertyGroup
{
    /// <summary>
    /// Gets the managed material property.
    /// </summary>
    public ManagedMaterialProperty Property { get; }

    /// <summary>
    /// Gets the native material property.
    /// </summary>
    public NativeMaterialProperty Native { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleMaterialPropertyGroup"/> class.
    /// </summary>
    /// <param name="managedProperty">The managed material property associated with this property group.</param>
    /// <param name="nativeProperty">The native material property associated with this property group.</param>
    public SimpleMaterialPropertyGroup(ManagedMaterialProperty managedProperty, NativeMaterialProperty nativeProperty)
    {
        this.Property = managedProperty;
        this.Native = nativeProperty;
    }

    /// <summary>
    /// Creates a new <see cref="SimpleMaterialPropertyGroup"/> from the given <see cref="NativeMaterialProperty"/>.
    /// </summary>
    /// <param name="nativeProperty">The native property.</param>
    /// <returns>The managed property, or null if the property cannot be mapped.</returns>
    public static SimpleMaterialPropertyGroup? FromNative(NativeMaterialProperty nativeProperty)
    {
        var managedName = MaterialPropertyMapper.GetManagedName(nativeProperty);
        var managedType = MaterialPropertyMapper.GetManagedType(nativeProperty);

        var customAttributes = new List<CustomAttributeBuilder>();

        if (nativeProperty.Flags.HasFlag(ShaderPropertyFlags.HideInInspector))
        {
            var constructor = typeof(HideInInspectorAttribute).GetConstructor([])!;
            customAttributes.Add(new CustomAttributeBuilder(constructor, []));
        }

        if (nativeProperty.Flags.HasFlag(ShaderPropertyFlags.Normal))
        {
            var constructor = typeof(NormalMapAttribute).GetConstructor([])!;
            customAttributes.Add(new CustomAttributeBuilder(constructor, []));
        }

        if (nativeProperty.IsRange)
        {
            var rangeLimits = nativeProperty.RangeLimits.Value;
            var rangeConstructor = typeof(RangeAttribute).GetConstructors()[0];
            customAttributes.Add(new CustomAttributeBuilder
            (
                rangeConstructor,
                [
                    rangeLimits.x, rangeLimits.y, rangeConstructor.GetParameters()[2].DefaultValue
                ]
            ));
        }

        return managedType is null
            ? null
            : new SimpleMaterialPropertyGroup
            (
                new ManagedMaterialProperty(managedName, managedType, customAttributes),
                nativeProperty
            );
    }

    /// <inheritdoc />
    public override IEnumerable<ManagedMaterialProperty> GetManagedProperties()
    {
        yield return this.Property;
    }

    /// <inheritdoc />
    public override IEnumerable<NativeMaterialProperty> GetNativeProperties()
    {
        yield return this.Native;
    }

    /// <inheritdoc />
    public override void EmitInitializeSyncMemberDefaults(ILGenerator il)
    {
        if (!this.Native.HasDefaultValue)
        {
            return;
        }

        if (this.Native.IsTexture)
        {
            // texture defaults are provided non-statically
            return;
        }

        if (this.Property.Field is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //   <empty>
        il.EmitLoadArgument(0);

        // stack:
        //   this
        il.EmitLoadField(this.Property.Field);

        // stack:
        //   ISyncMember
        il.EmitInlineDefault(this.Property.Type, this.Native);

        // stack:
        //   ISyncMember
        //   T
        il.EmitSetProperty
        (
            this.Property.Field.FieldType,
            this.Property.Type.IsValueType ? "Value" : "Target"
        );
    }

    /// <inheritdoc />
    public override void EmitUpdateMaterial(ILGenerator il)
    {
        if (this.Native.IsTexture)
        {
            il.EmitTextureUpdateCall
            (
                this.Property,
                this.Native
            );
        }
        else
        {
            il.EmitSimpleUpdateCall
            (
                this.Property,
                this.Native
            );
        }
    }
}
