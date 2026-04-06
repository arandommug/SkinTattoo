using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gui;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using SkinTatoo.Services;

namespace SkinTatoo;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;

    private const string CommandName = "/skintatoo";

    private readonly Configuration config;

    // Interop
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService textureSwap;
    private readonly EmissiveCBufferHook emissiveHook;

    // Services
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PreviewService previewService;
    private readonly ModExportService modExportService;

    // HTTP
    private readonly DebugServer debugServer;

    // Project
    private readonly DecalProject project;

    // GUI
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;
    private readonly ModelEditorWindow modelEditorWindow;
    private readonly ModExportWindow modExportWindow;

    // Auto-save throttle
    private DateTime lastAutoSave = DateTime.MinValue;
    private const double AutoSaveIntervalSec = 30.0;

    // Auto-load mesh on init
    private readonly IFramework framework;
    private bool autoLoadAttempted;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop)
    {
        // 1. Config
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // 2. Interop
        penumbra = new PenumbraBridge(pluginInterface, log);
        textureSwap = new TextureSwapService(objectTable, log);
        emissiveHook = new EmissiveCBufferHook(gameInterop, log);

        // 3. Services
        meshExtractor = new MeshExtractor(dataManager, log);
        imageLoader = new DecalImageLoader(log, dataManager);

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            meshExtractor, imageLoader,
            penumbra, textureSwap, emissiveHook, log, config, outputDir);

        var exportTempDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "export_temp");
        modExportService = new ModExportService(previewService, penumbra, NotificationManager, log, exportTempDir);

        // 4. Project - restore from config
        project = new DecalProject();
        project.LoadFromConfig(config);

        // 5. HTTP
        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, modExportService, textureSwap);
        debugServer.Start();

        // 6. GUI
        mainWindow = new MainWindow(project, previewService, penumbra, config, textureProvider);
        configWindow = new ConfigWindow(config);
        debugWindow = new DebugWindow();

        // 3D Editor
        modelEditorWindow = new ModelEditorWindow(project, previewService, penumbra, pluginInterface.UiBuilder.DeviceHandle);

        modExportWindow = new ModExportWindow(project, modExportService, config);

        mainWindow.DebugWindowRef = debugWindow;
        mainWindow.ConfigWindowRef = configWindow;
        mainWindow.ModelEditorWindowRef = modelEditorWindow;
        mainWindow.ModExportWindowRef = modExportWindow;
        mainWindow.OnSaveRequested += SaveProject;

        windowSystem = new WindowSystem("SkinTatoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(debugWindow);
        windowSystem.AddWindow(modelEditorWindow);
        windowSystem.AddWindow(modExportWindow);

        // Restore window open states
        mainWindow.IsOpen = config.MainWindowOpen;
        debugWindow.IsOpen = config.DebugWindowOpen;
        modelEditorWindow.IsOpen = config.ModelEditorWindowOpen;

        // 7. UiBuilder hooks
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        // 8. Chat command
        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTatoo 纹身编辑器窗口",
        });

        this.framework = framework;
        framework.Update += OnFrameworkUpdate;

        log.Information("SkinTatoo 已加载。Penumbra={0}", penumbra.IsAvailable);
    }

    private void DrawUi()
    {
        // Only draw when logged into the game world
        if (ObjectTable.LocalPlayer == null) return;

        windowSystem.Draw();

        // Periodic auto-save
        var now = DateTime.UtcNow;
        if ((now - lastAutoSave).TotalSeconds >= AutoSaveIntervalSec)
        {
            lastAutoSave = now;
            SaveWindowStates();
        }
    }

    private void SaveProject()
    {
        project.SaveToConfig(config);
        SaveWindowStates();
    }

    private void SaveWindowStates()
    {
        config.MainWindowOpen = mainWindow.IsOpen;
        config.DebugWindowOpen = debugWindow.IsOpen;
        config.ModelEditorWindowOpen = modelEditorWindow.IsOpen;
        config.Save();
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (autoLoadAttempted) return;
        if (ObjectTable.LocalPlayer == null) return;

        autoLoadAttempted = true;
        // One-shot: unsubscribe so we don't keep paying the per-frame check cost
        framework.Update -= OnFrameworkUpdate;

        foreach (var group in project.Groups)
        {
            if (group.AllMeshPaths.Count > 0 && previewService.CurrentMesh == null)
            {
                previewService.LoadMeshes(group.AllMeshPaths);
                modelEditorWindow.OnMeshChanged();
                break;
            }
        }

        // Auto-apply decals if there are configured groups with layers
        var hasLayers = false;
        foreach (var group in project.Groups)
        {
            if (!string.IsNullOrEmpty(group.DiffuseGamePath) && group.Layers.Count > 0)
            { hasLayers = true; break; }
        }
        if (hasLayers && config.AutoPreview)
        {
            previewService.UpdatePreview(project);
            modelEditorWindow.MarkTexturesDirty();
        }
    }

    public void Dispose()
    {
        // Save state before teardown
        project.SaveToConfig(config);
        SaveWindowStates();

        // OnFrameworkUpdate may have already unsubscribed itself; -= is safe either way
        framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.OnSaveRequested -= SaveProject;
        mainWindow.Dispose();
        modelEditorWindow.Dispose();

        debugServer.Dispose();

        modExportService.Dispose();
        previewService.Dispose();
        emissiveHook.Dispose();

        penumbra.Dispose();
    }
}
