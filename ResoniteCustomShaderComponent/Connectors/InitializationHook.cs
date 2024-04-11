//
//  SPDX-FileName: InitializationHook.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.ComponentModel;
using FrooxEngine;
using HarmonyLib;

[module: Description("FROOXENGINE_WEAVED")]

namespace ResoniteCustomShaderComponent.Connectors;

/// <summary>
/// Acts as a fake connector, letting us run code at FrooxEngine's initialization time.
/// </summary>
[ImplementableClass(true)]
internal static class InitializationHook
{
    private static Type? __connectorType;
    private static Type? __connectorTypes;

    private static InitializationHookConnector InstantiateConnector() => new();

    static InitializationHook()
    {
        try
        {
            var harmony = new Harmony("nu.algiz.resonite.custom-shaders");
            harmony.PatchAll();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private sealed class InitializationHookConnector : IConnector
    {
        public void AssignOwner(IImplementable owner) => this.Owner = owner;

        public void RemoveOwner() => this.Owner = null;

        public void Initialize()
        {
        }

        public void ApplyChanges()
        {
        }

        public void Destroy(bool destroyingWorld)
        {
        }

        public IImplementable? Owner { get; private set; }
    }
}
