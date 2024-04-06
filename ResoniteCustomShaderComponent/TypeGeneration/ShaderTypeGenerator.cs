//
//  SPDX-FileName: ShaderTypeGenerator.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using ResoniteCustomShaderComponent.Shaders;
using StrictEmit;
using UnityEngine;
using UnityEngine.Rendering;
using Material = FrooxEngine.Material;
using RangeAttribute = FrooxEngine.RangeAttribute;
using Rect = Elements.Core.Rect;
using Shader = UnityEngine.Shader;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Generates FrooxEngine material types based on unity shaders.
/// </summary>
internal static class ShaderTypeGenerator
{
    private const string _dynamicAssemblyName = "__customShaders";
    private const string _dynamicModuleName = "__customShaderModule";

    /// <summary>
    /// Gets the ABI version of generated shaders.
    /// </summary>
    public static Version GeneratedShaderVersion { get; } = new(1, 0, 0);

    /// <summary>
    /// Defines a dynamic shader type based on the given Unity shader.
    /// </summary>
    /// <param name="shaderCacheDirectory">The directory in which generated shaders are cached.</param>
    /// <param name="shaderHash">The Resonite cloud URI to the shader.</param>
    /// <param name="shader">The Unity shader that corresponds to the URI.</param>
    /// <returns>The shader type.</returns>
    public static Type DefineDynamicShaderType(string shaderCacheDirectory, string shaderHash, Shader shader)
    {
        UniLog.Log($"Creating new dynamic shader type for {shaderHash}");
        var dynamicAssemblyName = new AssemblyName(_dynamicAssemblyName)
        {
            Version = GeneratedShaderVersion
        };

        var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly
        (
            dynamicAssemblyName,
            AssemblyBuilderAccess.RunAndSave,
            shaderCacheDirectory
        );

        var cachedShaderFilename = $"{shaderHash}.dll";
        var dynamicModule = dynamicAssembly.DefineDynamicModule
        (
            _dynamicModuleName,
            cachedShaderFilename,
            false
        );

        var typeBuilder = dynamicModule.DefineType
        (
            shaderHash,
            TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(DynamicShader)
        );

        var materialPropertyFields = typeBuilder.DefineDynamicMaterialPropertyFields(shader);
        var propertyMembersField = typeBuilder.EmitGetMaterialPropertyMembers();
        var propertyMemberNamesField = typeBuilder.EmitGetMaterialPropertyNames();

        typeBuilder.EmitConstructor(materialPropertyFields, propertyMembersField, propertyMemberNamesField);

        typeBuilder.EmitInitializeSyncMemberDefaults(materialPropertyFields);
        typeBuilder.EmitUpdateMaterial(materialPropertyFields, propertyMemberNamesField);

        var type = typeBuilder.CreateType();

        // Cache the generated shader
        Directory.CreateDirectory(shaderCacheDirectory!);
        dynamicAssembly.Save(cachedShaderFilename);

        return type;
    }

    /// <summary>
    /// Defines <see cref="Sync{T}"/> fields for each material property in the given shader.
    /// </summary>
    /// <param name="typeBuilder">The type builder to define the fields in.</param>
    /// <param name="shader">The Unity shader to define fields for.</param>
    /// <returns>The defined fields.</returns>
    private static IReadOnlyList<(FieldBuilder Field, ManagedMaterialProperty Property)> DefineDynamicMaterialPropertyFields
    (
        this TypeBuilder typeBuilder,
        Shader shader
    )
    {
        var nativeProperties = NativeMaterialProperty.GetProperties(shader);
        var managedProperties = nativeProperties
            .Select(SimpleMaterialProperty.FromNative)
            .Where(p => p is not null)
            .OfType<SimpleMaterialProperty>()
            .ToArray(); // poor man's null suppression

        var materialPropertyFields = new List<FieldBuilder>();
        foreach (var managedProperty in managedProperties)
        {
            if (managedProperty.IsHidden)
            {
                UniLog.Log($"Shader property \"{managedProperty.Name}\" is marked as HideInInspector - skipping");
                continue;
            }

            UniLog.Log
            (
                $"Adding shader property \"{managedProperty.Name}\" with shader type \"{managedProperty.NativeProperty.Type}\" and runtime type "
                + $"\"{managedProperty.Type}\""
            );

            var propertyRefType = managedProperty.Type.IsValueType ? typeof(Sync<>) : typeof(AssetRef<>);

            var fieldBuilder = typeBuilder.DefineField
            (
                managedProperty.Name,
                propertyRefType.MakeGenericType(managedProperty.Type),
                FieldAttributes.Public | FieldAttributes.InitOnly
            );

            if (managedProperty.NativeProperty.IsRange)
            {
                var rangeLimits = managedProperty.NativeProperty.RangeLimits.Value;
                var rangeConstructor = typeof(RangeAttribute).GetConstructors()[0];
                var attributeBuilder = new CustomAttributeBuilder
                (
                    rangeConstructor,
                    [
                        rangeLimits.x, rangeLimits.y, rangeConstructor.GetParameters()[2].DefaultValue
                    ]
                );

                UniLog.Log($"Adding range limitations {rangeLimits.x:F2} to {rangeLimits.y:F2} (display rounded)");
                fieldBuilder.SetCustomAttribute(attributeBuilder);
            }

            materialPropertyFields.Add(fieldBuilder);
        }

        return materialPropertyFields.Zip
        (
            managedProperties,
            (field, property) => (field, (ManagedMaterialProperty)property)
        ).ToList();
    }

    private static void EmitInitializeSyncMemberDefaults
    (
        this TypeBuilder typeBuilder,
        IEnumerable<(FieldBuilder Field, ManagedMaterialProperty Property)> materialPropertyFields
    )
    {
        var initializeSyncMemberDefaults = typeBuilder.DefineMethod
        (
            "InitializeSyncMemberDefaults",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(void),
            []
        );

        typeBuilder.DefineMethodOverride
        (
            initializeSyncMemberDefaults,
            typeof(MaterialProvider).GetMethod("InitializeSyncMemberDefaults", BindingFlags.Instance | BindingFlags.NonPublic)!
        );

        var initializeSyncMemberDefaultsIL = initializeSyncMemberDefaults.GetILGenerator();
        foreach (var (field, property) in materialPropertyFields)
        {
            if (!property.NativeProperty.HasDefaultValue)
            {
                continue;
            }

            if (property.NativeProperty.IsTexture)
            {
                // texture defaults are provided non-statically
                continue;
            }

            // stack:
            //   <empty>
            initializeSyncMemberDefaultsIL.EmitLoadArgument(0);

            // stack:
            //   this
            initializeSyncMemberDefaultsIL.EmitLoadField(field);

            // stack:
            //   ISyncMember
            initializeSyncMemberDefaultsIL.EmitInlineDefault(property);

            // stack:
            //   ISyncMember
            //   T
            initializeSyncMemberDefaultsIL.EmitSetProperty(field.FieldType, property.Type.IsValueType ? "Value" : "Target");
        }

        // stack:
        //   <empty>
        initializeSyncMemberDefaultsIL.EmitReturn();
    }

    private static void EmitInlineDefault(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.Type.IsScalar() && managedMaterialProperty.NativeProperty.IsScalar)
        {
            generator.EmitDefaultScalar(managedMaterialProperty);
        }
        else if (managedMaterialProperty.NativeProperty.IsVector)
        {
            generator.EmitDefaultVectorLike(managedMaterialProperty);
        }
        else if (managedMaterialProperty.Type.IsEnum)
        {
            generator.EmitDefaultEnum(managedMaterialProperty);
        }
    }

    private static void EmitDefaultEnum(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.NativeProperty.DefaultValue is null)
        {
            throw new ArgumentNullException(nameof(managedMaterialProperty.NativeProperty.DefaultValue));
        }

        var backingType = managedMaterialProperty.Type.GetEnumUnderlyingType();
        if (backingType == typeof(long) || backingType == typeof(ulong))
        {
            generator.EmitConstantLong((long)managedMaterialProperty.NativeProperty.DefaultValue.Value);
        }
        else
        {
            generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue.Value);
        }
    }

    private static void EmitDefaultVectorLike(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.NativeProperty.DefaultVector is null)
        {
            throw new ArgumentNullException(nameof(managedMaterialProperty.NativeProperty.DefaultVector));
        }

        var elementCount = managedMaterialProperty.Type switch
        {
            var type when type == typeof(float2) => 2,
            var type when type == typeof(float3) => 3,
            var type when type == typeof(float4) => 4,
            var type when type == typeof(colorX) => 4,
            var type when type == typeof(Rect) => 4,
            _ => throw new ArgumentOutOfRangeException()
        };

        var elementConstructor = managedMaterialProperty.Type switch
        {
            var type when type == typeof(float2) => typeof(float2).GetConstructor([typeof(float), typeof(float)])!,
            var type when type == typeof(float3) => typeof(float3).GetConstructor([typeof(float), typeof(float), typeof(float)])!,
            var type when type == typeof(float4) => typeof(float4).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            var type when type == typeof(colorX) => typeof(colorX).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float), typeof(ColorProfile)])!,
            var type when type == typeof(Rect) => typeof(Rect).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            _ => throw new ArgumentOutOfRangeException()
        };

        for (var i = 0; i < elementCount; i++)
        {
            generator.EmitConstantFloat(managedMaterialProperty.NativeProperty.DefaultVector.Value[i]);
        }

        if (managedMaterialProperty.Type == typeof(colorX))
        {
            generator.EmitConstantInt((int)ColorProfile.sRGB);
        }

        generator.EmitNewObject(elementConstructor);
    }

    private static void EmitDefaultTexture(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.NativeProperty.DefaultTextureName is null)
        {
            throw new ArgumentNullException(nameof(managedMaterialProperty.NativeProperty.DefaultTextureName));
        }

        generator.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        generator.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (managedMaterialProperty.NativeProperty.DefaultTextureName.ToLowerInvariant())
        {
            case "white":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.WhiteTexture));
                break;
            }
            case "black":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.BlackTexture));
                break;
            }
            case "clear":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.ClearTexture));
                break;
            }
            case "darkchecker":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerTexture));
                break;
            }
            default:
            {
                generator.EmitLoadNull();
                break;
            }
        }
    }

    private static void EmitDefaultCubemap(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.NativeProperty.DefaultTextureName is null)
        {
            throw new ArgumentNullException(nameof(managedMaterialProperty.NativeProperty.DefaultTextureName));
        }

        generator.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        generator.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (managedMaterialProperty.NativeProperty.DefaultTextureName.ToLowerInvariant())
        {
            case "darkchecker":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerCubemap));
                break;
            }
            default:
            {
                generator.EmitLoadNull();
                break;
            }
        }
    }

    private static void EmitDefaultScalar(this ILGenerator generator, ManagedMaterialProperty managedMaterialProperty)
    {
        if (managedMaterialProperty.NativeProperty.DefaultValue is null)
        {
            throw new ArgumentNullException(nameof(managedMaterialProperty.NativeProperty.DefaultValue));
        }

        switch (managedMaterialProperty.Type)
        {
            case var _ when managedMaterialProperty.Type == typeof(sbyte):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToByte();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(byte):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToUByte();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(ushort):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToUShort();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(short):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToShort();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(uint):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToUInt();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(int):
            {
                generator.EmitConstantInt((int)managedMaterialProperty.NativeProperty.DefaultValue);
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(ulong):
            {
                generator.EmitConstantLong((long)managedMaterialProperty.NativeProperty.DefaultValue);
                generator.EmitConvertToULong();
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(long):
            {
                generator.EmitConstantLong((long)managedMaterialProperty.NativeProperty.DefaultValue);
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(float):
            {
                generator.EmitConstantFloat(managedMaterialProperty.NativeProperty.DefaultValue.Value);
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(double):
            {
                generator.EmitConstantDouble(managedMaterialProperty.NativeProperty.DefaultValue.Value);
                break;
            }
            case var _ when managedMaterialProperty.Type == typeof(decimal):
            {
                generator.EmitConstantFloat(managedMaterialProperty.NativeProperty.DefaultValue.Value);
                generator.EmitCallDirect<decimal>("op_Implicit", typeof(float));
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(managedMaterialProperty));
            }
        }
    }

    private static void EmitUpdateMaterial
    (
        this TypeBuilder typeBuilder,
        IEnumerable<(FieldBuilder Field, ManagedMaterialProperty Property)> materialPropertyFields,
        FieldInfo propertyMemberNamesField
    )
    {
        var updateMaterial = typeBuilder.DefineMethod
        (
            "UpdateMaterial",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(void),
            [typeof(Material)]
        );

        typeBuilder.DefineMethodOverride
        (
            updateMaterial,
            typeof(MaterialProvider).GetMethod("UpdateMaterial", BindingFlags.Instance | BindingFlags.NonPublic)!
        );

        var indexerProperty = typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>)
            .GetProperties()
            .Single(p => p.GetIndexParameters().Length > 0);

        var implicitConversion = typeof(MaterialProperty)
            .GetMethods()
            .Single(m => m.Name is "op_Implicit" && m.ReturnType == typeof(int));

        var updateMaterialIL = updateMaterial.GetILGenerator();
        foreach (var (field, property) in materialPropertyFields)
        {
            // material.UpdateXXX(property, field);
            if (property.NativeProperty.IsTexture)
            {
                EmitTextureUpdateCall(propertyMemberNamesField, updateMaterialIL, field, indexerProperty, implicitConversion, property);
            }
            else
            {
                EmitSimpleUpdateCall(propertyMemberNamesField, updateMaterialIL, field, indexerProperty, implicitConversion, property);
            }
        }

        // stack:
        //  <empty>
        updateMaterialIL.EmitReturn();
    }

    private static void EmitTextureUpdateCall
    (
        FieldInfo propertyMemberNamesField,
        ILGenerator updateMaterialIL,
        FieldInfo field,
        PropertyInfo indexerProperty,
        MethodInfo implicitConversion,
        ManagedMaterialProperty property
    )
    {
        // stack:
        //  <empty>
        updateMaterialIL.EmitLoadArgument(1);

        // stack:
        //  Material
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  this
        updateMaterialIL.EmitLoadField(propertyMemberNamesField);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        //  this
        updateMaterialIL.EmitLoadField(field);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        //  ISyncMember
        updateMaterialIL.EmitCallVirtual(indexerProperty.GetMethod);

        // stack:
        //  Material
        //  MaterialProperty
        updateMaterialIL.EmitCallDirect(implicitConversion);

        // stack:
        //  Material
        //  int
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        updateMaterialIL.EmitLoadField(field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        if (property.NativeProperty.TextureDimension is TextureDimension.Cube)
        {
            updateMaterialIL.EmitDefaultCubemap(property);
        }
        else
        {
            updateMaterialIL.EmitDefaultTexture(property);
        }

        // stack:
        //  Material
        //  int
        //  ISyncMember
        //  ITexture2D? | Cubemap?
        updateMaterialIL.EmitCallVirtual(MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(property));
    }

    private static void EmitSimpleUpdateCall
    (
        FieldInfo propertyMemberNamesField,
        ILGenerator updateMaterialIL,
        FieldInfo field,
        PropertyInfo indexerProperty,
        MethodInfo implicitConversion,
        ManagedMaterialProperty property
    )
    {
        // stack:
        //  <empty>
        updateMaterialIL.EmitLoadArgument(1);

        // stack:
        //  Material
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  this
        updateMaterialIL.EmitLoadField(propertyMemberNamesField);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        //  this
        updateMaterialIL.EmitLoadField(field);

        // stack:
        //  Material
        //  IReadOnlyDictionary
        //  ISyncMember
        updateMaterialIL.EmitCallVirtual(indexerProperty.GetMethod);

        // stack:
        //  Material
        //  MaterialProperty
        updateMaterialIL.EmitCallDirect(implicitConversion);

        // stack:
        //  Material
        //  int
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        updateMaterialIL.EmitLoadField(field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        updateMaterialIL.EmitCallVirtual(MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(property));
    }

    /// <summary>
    /// Defines and emits a constructor that initializes each material property field, along with storing them in the
    /// property members field and their material property names in the property names field.
    /// </summary>
    /// <param name="typeBuilder">The type to define the constructor in.</param>
    /// <param name="materialPropertyFields">The fields defined for the shader's material properties.</param>
    /// <param name="propertyMembersField">The field that holds a list of each material property field.</param>
    /// <param name="propertyNamesField">
    /// The field that holds a mapping of each material property's <see cref="Sync{T}"/> value to its material property
    /// name.
    /// </param>
    private static void EmitConstructor
    (
        this TypeBuilder typeBuilder,
        IEnumerable<(FieldBuilder Field, ManagedMaterialProperty Property)> materialPropertyFields,
        FieldInfo propertyMembersField,
        FieldInfo propertyNamesField
    )
    {
        var constructorBuilder = typeBuilder.DefineConstructor
        (
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );

        var constructorIL = constructorBuilder.GetILGenerator();
        var syncMemberListLocal = constructorIL.DeclareLocal(typeof(List<ISyncMember>));
        var syncMemberNameMapLocal = constructorIL.DeclareLocal(typeof(Dictionary<ISyncMember, MaterialProperty>));

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        var baseConstructor = typeof(DynamicShader).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, [])!;
        constructorIL.EmitCallDirect(baseConstructor);

        // stack: <empty>
        constructorIL.EmitNewObject<List<ISyncMember>>();

        // stack:
        //   List<ISyncMember>
        constructorIL.EmitSetLocalVariable(syncMemberListLocal);

        // stack: <empty>
        constructorIL.EmitNewObject<Dictionary<ISyncMember, MaterialProperty>>();

        // stack:
        //   Dictionary<ISyncMember, MaterialProperty>
        constructorIL.EmitSetLocalVariable(syncMemberNameMapLocal);

        foreach (var (field, property) in materialPropertyFields)
        {
            var syncFieldConstructor = field.FieldType.GetConstructor([])!;

            // create syncField, store it in a local
            var local = constructorIL.DeclareLocal(field.FieldType);
            constructorIL.EmitNewObject(syncFieldConstructor);
            constructorIL.EmitSetLocalVariable(local);

            // stack: <empty>
            constructorIL.EmitLoadLocalVariable(syncMemberListLocal);

            // stack:
            //   List<ISyncMember>
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   List<ISyncMember>
            //   ISyncMember
            constructorIL.EmitCallVirtual<List<ISyncMember>>(nameof(List<ISyncMember>.Add));

            // stack: <empty>
            constructorIL.EmitLoadLocalVariable(syncMemberNameMapLocal);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            constructorIL.EmitConstantString(property.NativeProperty.Name);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   string
            constructorIL.EmitNewObject<MaterialProperty>(typeof(string));

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   MaterialProperty
            constructorIL.EmitCallVirtual<Dictionary<ISyncMember, MaterialProperty>>(nameof(Dictionary<ISyncMember, MaterialProperty>.Add));

            // stack: <empty>
            constructorIL.EmitLoadArgument(0);

            // stack:
            //   this
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   this
            //   ISyncMember
            constructorIL.EmitSetField(field);
        }

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        constructorIL.EmitLoadLocalVariable(syncMemberListLocal);

        // stack:
        //   this
        //   List<ISyncMember>
        constructorIL.EmitSetField(propertyMembersField);

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        constructorIL.EmitLoadLocalVariable(syncMemberNameMapLocal);

        // stack:
        //   this
        //   Dictionary<ISyncMember, MaterialProperty>
        constructorIL.EmitSetField(propertyNamesField);

        // stack: <empty>
        constructorIL.EmitReturn();
    }

    /// <summary>
    /// Defines and emits an implementing method for <see cref="DynamicShader.GetMaterialPropertyMembers"/>, along with
    /// its corresponding backing field.
    /// </summary>
    /// <param name="typeBuilder">The type to emit the method in.</param>
    private static FieldBuilder EmitGetMaterialPropertyMembers(this TypeBuilder typeBuilder)
    {
        var propertyMembersField = typeBuilder.DefineField
        (
            "_materialProperties",
            typeof(IReadOnlyList<ISyncMember>),
            FieldAttributes.Private
        );

        var getMaterialPropertyMembers = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyMembers),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyList<ISyncMember>),
            []
        );

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyMembers,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyMembers))!
        );

        var getMaterialPropertyMembersIL = getMaterialPropertyMembers.GetILGenerator();
        getMaterialPropertyMembersIL.EmitLoadArgument(0);
        getMaterialPropertyMembersIL.EmitLoadField(propertyMembersField);
        getMaterialPropertyMembersIL.EmitReturn();

        return propertyMembersField;
    }

    /// <summary>
    /// Defines and emits an implementing method for <see cref="DynamicShader.GetMaterialPropertyNames"/>, along with
    /// its corresponding backing field.
    /// </summary>
    /// <param name="typeBuilder">The type to emit the method in.</param>
    private static FieldBuilder EmitGetMaterialPropertyNames(this TypeBuilder typeBuilder)
    {
        var propertyNamesField = typeBuilder.DefineField
        (
            "_materialPropertyNames",
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            FieldAttributes.Private
        );

        var getMaterialPropertyNames = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyNames),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            []
        );

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyNames,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyNames))!
        );

        var getMaterialPropertyNamesIL = getMaterialPropertyNames.GetILGenerator();
        getMaterialPropertyNamesIL.EmitLoadArgument(0);
        getMaterialPropertyNamesIL.EmitLoadField(propertyNamesField);
        getMaterialPropertyNamesIL.EmitReturn();

        return propertyNamesField;
    }
}
