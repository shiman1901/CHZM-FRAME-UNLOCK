using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>Результат умного автопилота для конкретной игры.</summary>
public sealed record AutoPilotInfo(
    bool Blocked,
    string BlockReason,
    EngineMode? Recommended,
    string Message,
    bool HasFrameGen,
    bool ManualOverride,
    bool ClassicAvailable,
    bool LiveFlowRecommended,
    bool LiveFlowAvailable,
    bool ProxyModesBlocked);

public sealed partial class TensorAutoService
{
    private static readonly ConcurrentDictionary<string, bool> NativeFrameGenCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сбросить кэш определения DLSS-G (нужно автотесту и после обновления игры).</summary>
    public static void ResetNativeFrameGenCache() => NativeFrameGenCache.Clear();

    /// <summary>
    /// Настоящий DLSS-G игры: nvngx_dlssg.dll или sl.dlss_g.dll.
    /// Наши собственные dlssg_sm86\ и OptiScaler\streamline\ намеренно исключены,
    /// иначе после установки Classic любая игра выглядела бы «поддерживающей DLSS-G».
    /// </summary>
    public static bool HasNativeDlssFrameGen(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return false;
        if (NativeFrameGenCache.TryGetValue(gameDirectory, out bool cached)) return cached;

        bool found = false;

        try
        {
            foreach (string dir in CandidateDirectories(gameDirectory))
            {
                if (IsOurModDirectory(dir)) continue;

                if (File.Exists(Path.Combine(dir, "nvngx_dlssg.dll")) ||
                    File.Exists(Path.Combine(dir, "sl.dlss_g.dll")))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                string root = GetGameRoot(gameDirectory);
                found = ContainsNativeDllBounded(root, new[] { "nvngx_dlssg.dll", "sl.dlss_g.dll" });
            }
        }
        catch
        {
            // не смогли определить — считаем, что DLSS-G нет
        }

        NativeFrameGenCache[gameDirectory] = found;
        return found;
    }

    /// <summary>
    /// Ограниченный обход дерева: не зависаем на гигантских установках.
    /// Глубина 8 и не больше 500 папок — этого хватает для UE Engine\Plugins\...,
    /// при этом наши dlssg_sm86\ и OptiScaler\ пропускаются.
    /// </summary>
    private static bool ContainsNativeDllBounded(string root, string[] fileNames)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        int visited = 0;

        while (queue.Count > 0 && visited < 500)
        {
            (string directory, int depth) = queue.Dequeue();
            visited++;

            if (IsOurModDirectory(directory)) continue;

            try
            {
                foreach (string name in fileNames)
                {
                    if (File.Exists(Path.Combine(directory, name))) return true;
                }
            }
            catch
            {
                // ignore
            }

            if (depth >= 8) continue;

            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (!IsOurModDirectory(child))
                        queue.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // нет прав на папку — пропускаем
            }
        }

        return false;
    }

    private static bool IsOurModDirectory(string path)
    {
        try
        {
            string[] parts = path.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (part.Equals("dlssg_sm86", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("dlssg_sm75", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("rtx-encore-logs", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("rtx-encore-captures", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>В папке игры уже стоит наш прокси (Classic или Encore/SM).</summary>
    public static bool HasInstalledEngine(string gameDirectory)
    {
        try
        {
            return HasModPayload(gameDirectory) ||
                   FindEncoreProxy(gameDirectory) is not null ||
                   File.Exists(Path.Combine(gameDirectory, EncoreConfigName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Античит или загрузчик античита (EAC / BattlEye / preloader.dll).
    /// preloader.dll проверяется отдельно: он есть у ARC Raiders и не всегда
    /// лежит рядом с EasyAntiCheat.
    /// </summary>
    public static bool HasAntiCheatOrPreloader(string gameDirectory, out string reason)
    {
        reason = string.Empty;

        // Сюжетка / sp26: внутренние прокси разрешены, античит не учитываем.
        if (IsSinglePlayerCampaign(gameDirectory)) return false;

        if (IsEacProtected(gameDirectory, out reason)) return true;

        try
        {
            string? dir = gameDirectory;

            for (int level = 0; level < 4 && !string.IsNullOrEmpty(dir); level++)
            {
                if (File.Exists(Path.Combine(dir, "preloader.dll")))
                {
                    reason = "preloader.dll";
                    return true;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>Только анализ, без изменения выбранного режима.</summary>
    public AutoPilotInfo EvaluateAutoPilot(ActiveGameInfo game)
    {
        bool antiCheat = HasAntiCheatOrPreloader(game.Directory, out string reason);
        bool proxyBlocked = antiCheat && !IgnoreAntiCheatWarnings;
        bool hasFrameGen = HasNativeDlssFrameGen(game.Directory);
        bool manual = HasManualMode(game.ExecutablePath);
        bool liveFlowAvailable = LiveFlowBridge.IsInstalled;
        bool installedEngine = HasInstalledEngine(game.Directory);

        // Онлайн-игры с античитом: внутренние прокси запрещены, но внешний
        // LIVE FLOW (DXGI + Optical Flow) не пишет файлы в игру и работает.
        if (antiCheat)
        {
            bool liveFlowForAntiCheat = liveFlowAvailable && !manual;

            string text = liveFlowForAntiCheat
                ? "⚡ LIVE FLOW 2X (Quality · B) — внешний захват DXGI, файлы игры не трогаются"
                : liveFlowAvailable
                    ? "⚡ LIVE FLOW доступен — выбран ручной режим для этой игры"
                    : "🛡 Античит: LIVE FLOW не найден, внутренние прокси недоступны";

            if (proxyBlocked)
                text += " · прокси: «Недоступно (Загрузчик античита)»";

            text += " · запуск на свой риск";

            return new AutoPilotInfo(
                Blocked: proxyBlocked,
                BlockReason: reason,
                Recommended: manual ? GetManualMode(game.ExecutablePath) : null,
                Message: text,
                HasFrameGen: hasFrameGen,
                ManualOverride: manual,
                ClassicAvailable: false,
                LiveFlowRecommended: liveFlowForAntiCheat,
                LiveFlowAvailable: liveFlowAvailable,
                ProxyModesBlocked: true);
        }

        EngineMode? recommended;

        if (hasFrameGen)
        {
            recommended = IsEncoreReady()
                ? EngineMode.RtxEncore
                : IsModSourceReady() ? EngineMode.ClassicSm86 : null;
        }
        else
        {
            recommended = IsEncoreReady() || IsSmoothMotionReady()
                ? EngineMode.SmoothMotion
                : null;
        }

        // Магия одной кнопки: если в игре ещё нет нашего прокси — LIVE FLOW
        // даёт буст сразу, без перезапуска и без файлов в игре.
        // Ручной выбор пользователя для этой игры всегда важнее автопилота.
        bool liveFlowRecommended = liveFlowAvailable && !manual && !installedEngine;

        string message;

        if (liveFlowRecommended)
        {
            message = hasFrameGen
                ? "⚡ Автовыбор: DLSS-G уже есть, но мод не установлен — LIVE FLOW 2X (Quality · B)"
                : "⚡ Автовыбор: в игре нет DLSS-G — LIVE FLOW 2X (Quality · B), DXGI + Optical Flow";
        }
        else if (hasFrameGen)
        {
            message = recommended == EngineMode.RtxEncore
                ? "✨ Автовыбор: игра поддерживает DLSS-G, выбран RTX Encore (4X/6X)"
                : "✨ Автовыбор: игра поддерживает 4X/6X на тензорных ядрах";
        }
        else
        {
            message = "✨ Автовыбор: в игре нет DLSS-G, выбран драйверный Smooth Motion";
        }

        if (manual)
            message += " · ручной выбор для этой игры";

        return new AutoPilotInfo(
            Blocked: false,
            BlockReason: string.Empty,
            Recommended: recommended,
            Message: message,
            HasFrameGen: hasFrameGen,
            ManualOverride: manual,
            ClassicAvailable: hasFrameGen,
            LiveFlowRecommended: liveFlowRecommended,
            LiveFlowAvailable: liveFlowAvailable,
            ProxyModesBlocked: false);
    }

    /// <summary>
    /// Применить автовыбор движка, если пользователь не назначил режим вручную.
    /// LIVE FLOW не стартует сам — его запускает большая кнопка или [N],
    /// чтобы оверлей не появился поверх игры без спроса.
    /// </summary>
    public AutoPilotInfo ApplyAutoPilot(ActiveGameInfo game)
    {
        AutoPilotInfo info = EvaluateAutoPilot(game);
        if (info.Blocked) return info;

        if (info.ManualOverride)
        {
            EngineMode? manualMode = GetManualMode(game.ExecutablePath);

            if (!info.HasFrameGen && manualMode == EngineMode.ClassicSm86)
            {
                ClearManualMode(game.ExecutablePath);
                SelectEngineAuto(EngineMode.SmoothMotion);
                return EvaluateAutoPilot(game);
            }

            return info;
        }

        if (info.Recommended is EngineMode recommended)
            SelectEngineAuto(recommended);

        return EvaluateAutoPilot(game);
    }
}

