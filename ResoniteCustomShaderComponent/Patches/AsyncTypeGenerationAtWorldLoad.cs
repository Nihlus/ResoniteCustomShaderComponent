//
//  SPDX-FileName: AsyncTypeGenerationAtWorldLoad.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Net;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteCustomShaderComponent.TypeGeneration;
using SkyFrost.Base;

#pragma warning disable SA1313

namespace ResoniteCustomShaderComponent.Patches;

/// <summary>
/// Replaces <see cref="Userspace.LoadWorld"/> with our own implementation that enables type generation at world load
/// time.
/// </summary>
[HarmonyPatch(typeof(Userspace), "LoadWorld")]
public static class AsyncTypeGenerationAtWorldLoad
{
    [HarmonyPrefix]
    private static bool Prefix(Userspace __instance, WorldStartSettings startInfo, out Task<World?> __result)
    {
        __result = LoadWorldWithTypeGeneration(__instance, startInfo);
        return false;
    }

    private static async Task<World?> LoadWorldWithTypeGeneration(this Userspace userspace, WorldStartSettings startInfo)
    {
        await default(ToBackground);

        var uri = startInfo.URIs.First<Uri>();
        Uri? assetUrl;
        if (uri.Scheme == userspace.Cloud.Platform.RecordScheme)
        {
            CloudResult<FrooxEngine.Store.Record> cloudResult;
            if (userspace.Cloud.Records.ExtractRecordID(uri, out var ownerId, out var recordId))
            {
                cloudResult = await userspace.Engine.RecordManager.FetchRecord(ownerId, recordId);
            }
            else
            {
                if (!userspace.Cloud.Records.ExtractRecordPath(uri, out ownerId, out string _))
                {
                    return null;
                }

                cloudResult = await userspace.Engine.RecordManager.FetchRecord(uri);
            }
            if (cloudResult.IsError)
            {
                if (cloudResult.State != HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (startInfo.Link?.CreateIfNotExists != null)
                {
                    var world = startInfo.Link.CreateIfNotExists(startInfo.Link);
                    world.AssignNewRecord(ownerId, recordId);
                    return world;
                }
                if (recordId == "R-Home")
                {
                    return await Userspace.CreateHome(IdUtil.GetOwnerType(ownerId), ownerId, startInfo);
                }
            }

            startInfo.Record = cloudResult.Entity;
            assetUrl = new Uri(startInfo.Record.AssetURI);
        }
        else
        {
            assetUrl = uri;
        }

        UniLog.Log("Requesting gather for: " + assetUrl);
        var str = await userspace.Engine.AssetManager.GatherAssetFile(assetUrl, 100f);
        if (!File.Exists(str))
        {
            UniLog.Log($"Failed the retrieve file for {assetUrl}, returned path: {str ?? "null"}");
            return null;
        }

        UniLog.Log("Got asset at path: " + str + ", loading world");
        var node = DataTreeConverter.Load(str, assetUrl);

        await DynamicShaderRepository.EnsureDynamicShaderTypesAsync(node);

        await default(ToWorld);

        return Userspace.StartSession(startInfo.InitWorld, startInfo.ForcePort, startInfo.ForceSessionId, node, false, startInfo.UnsafeMode);
    }
}
