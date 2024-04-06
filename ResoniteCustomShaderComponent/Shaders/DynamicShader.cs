//
//  SPDX-FileName: DynamicShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace ResoniteCustomShaderComponent.Shaders;

/// <summary>
/// Represents a set of dynamically generated shader properties.
/// </summary>
public abstract class DynamicShader : SingleShaderMaterialProvider
{
    [HideInInspector]
    private readonly SyncRef<IComponent?> _persistentDrive = new();

    [HideInInspector]
    private readonly SyncRef<IComponent?> _enabledDrive = new();

    [HideInInspector]
    private readonly SyncRef<IComponent?> _updateOrderDrive = new();

    [HideInInspector]
    private readonly Sync<Uri?> _shaderUrl = new();

    /// <inheritdoc />
    public override PropertyState PropertyInitializationState { get; protected set; }

    /// <inheritdoc />
    protected override Uri? ShaderURL => _shaderUrl;

    /// <inheritdoc />
    protected DynamicShader()
    {
    }

    /// <summary>
    /// Sets the URL of the shader wrapped by the dynamic shader.
    /// </summary>
    /// <param name="shaderUrl">The shader URL.</param>
    internal void SetShaderURL(Uri shaderUrl) => _shaderUrl.Value = shaderUrl;

    /// <summary>
    /// Gets a list of <see cref="ISyncMember"/> values mapping to the shader's defined properties.
    /// </summary>
    /// <returns>The property members.</returns>
    public abstract IReadOnlyList<ISyncMember> GetMaterialPropertyMembers();

    /// <summary>
    /// Gets a mapping between the material properties and their names.
    /// </summary>
    /// <returns>The mapping.</returns>
    public abstract IReadOnlyDictionary<ISyncMember, MaterialProperty> GetMaterialPropertyNames();

    /// <inheritdoc />
    protected override void GetMaterialProperties(List<MaterialProperty> properties)
    {
        properties.AddRange(GetMaterialPropertyNames().Values);
    }

    /// <inheritdoc />
    protected override void UpdateKeywords(ShaderKeywords keywords)
    {
        //throw new NotImplementedException();
    }

    /// <summary>
    /// Builds UI in an inspector, nested underneath an outer <see cref="CustomShader"/> component. This filters out a
    /// few internal properties that shouldn't be visible.
    /// </summary>
    /// <param name="ui">The UI builder.</param>
    public void BuildNestedInspectorUI(UIBuilder ui)
    {
        WorkerInspector.BuildInspectorUI
        (
            this,
            ui,
            m => m.Name is not
            (
                "persistent" or
                "Enabled" or
                "UpdateOrder" or
                "_shader"
            )
        );

        var materialAssetMetadata = ui.Root.AttachComponent<MaterialAssetMetadata>();
        materialAssetMetadata.Material.Target = this;

        ui.Text("Inspector.Material.VariantInfo".AsLocaleKey(("variantID", materialAssetMetadata.VariantID), ("rawVariantID", materialAssetMetadata.RawVariantID)));
        ui.Text("Inspector.Material.WaitingForApply".AsLocaleKey(null, "waiting", materialAssetMetadata.WaitingForApply));
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        base.OnDestroy();

        _persistentDrive.Target?.Destroy();
        _updateOrderDrive.Target?.Destroy();
        _enabledDrive.Target?.Destroy();
    }

    /// <summary>
    /// Drives the shader's control fields (Persistent, UpdateOrder, Enabled) from the upstream
    /// <see cref="CustomShader"/> component.
    /// </summary>
    /// <param name="upstreamPersistent">The upstream Persistent field.</param>
    /// <param name="upstreamUpdateOrder">The upstream UpdateOrder field.</param>
    /// <param name="upstreamEnabled">The upstream Enabled field.</param>
    public void DriveControlFields(Sync<bool> upstreamPersistent, Sync<int> upstreamUpdateOrder, Sync<bool> upstreamEnabled)
    {
        _persistentDrive.Target?.Destroy();
        _persistentDrive.Target = this.persistent.DriveFrom(upstreamPersistent);

        _updateOrderDrive.Target?.Destroy();
        _updateOrderDrive.Target = updateOrder.DriveFrom(upstreamUpdateOrder);

        _enabledDrive.Target?.Destroy();
        _enabledDrive.Target = this.EnabledField.DriveFrom(upstreamEnabled);
    }
}
