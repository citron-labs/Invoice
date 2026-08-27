using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace RevitFlexConduit;

public sealed class App301 : IExternalApplication
{
    internal const string PanelName = "Flex Conduit";
    internal const string ProductVersion = "3.0.1";

    // Revit-style flexible metallic conduit icon, generated specifically for this tool.
    // Transparent PNGs are embedded so no external image files are required.
    private const string Icon32Base64 = "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";
    private const string Icon16Base64 = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAACf0lEQVR4nIWTXUhTYRjH/+975s7m1/xoRhgmokVEYH5kkRFWdBPdKGeUJN0EGaZ0L3Q8FxKK1UVQQTeRH5gTilpiRSISmh9LTZsrY+KGMz/mlM22tp3zduFHpJ56bt/3/b3P//9/HmBLEQCUEjDGOFEU6dbzbffr7z2sTUlJTiWg3Gfb16k7tdX3AQQA+AFAEATObDbLqoQh60iIrZel4224v3/IZR0edT563Ni0NyP38AZEFfDy9XuH3TETnF1YCba2W9wf+qyKfzXAwuEI6xsYWRGu3DgPAGpyNIvLvj1wuPlwWMbwl+/aj9axpaWVQHJaqlHJyT4UXyoUPxvqH8mpqamZlAAKSVL+Alg6uyQNx6UFgkGDx+NxDfb2Ntvt386UXhRu7UpKjDt96lhMScmFSkJIJWOMEkn6t6uUEgBASWl51eAnG/N6l5W7D56OAojiKAXWgvrTQVtb26ZBZrMZ8/PzpKKigpkuX39TfrUspNWma+Njo1MBvVFhAfc6gG0CTCbT1ohIT08PA0v4IUdkb5RGs5vX6XjeYNQ1nivg6hyJtKHhEgWA7u7unWUwxggA+qTlxbDft6r0DYwqeUVC2Y4pqFkBQJ51zw0EQ5HsA5n75JvXym43xejn9AmxtPB4XoaGUGafnPKpvBcpIQRZR84efG7pCns8XsXvX2Xj4zbW0fnOtzF4re2vfql0ICmKwighZKK5JasaTK7bn5EOLR8NgyFpYW7RF+V0uUNjtskJNQkghCiiKFJJkuqnnS7riZOFVTqdPj8uRk9nFn/yHAWvgGYSNcCmGJFRSSIb02csKCrOz805Gh1RQlqnc/q/2wpgbZkYY5SQ7f/9BtJTHOZpyKl5AAAAAElFTkSuQmCC";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            FlexConduitV3Updater.RegisterApplicationUpdater();
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            RibbonPanel panel = application.GetRibbonPanels()
                .FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase))
                ?? application.CreateRibbonPanel(PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            if (panel.GetItems().All(i => !string.Equals(i.Name, "RevitFlexConduit.CreateV301", StringComparison.OrdinalIgnoreCase)))
            {
                var createData = new PushButtonData(
                    "RevitFlexConduit.CreateV301",
                    "Flex\nConduit",
                    assemblyPath,
                    typeof(FlexConduitV301Command).FullName!);
                createData.ToolTip = $"Flex Conduit v{ProductVersion} — connector-aware 3D spline raceway with persistent editable control points.";
                createData.LongDescription = "Start/end from electrical connectors, conduit endpoints, or free XYZ points. Control points remain editable after creation and connected equipment movement updates bound endpoints.";
                createData.LargeImage = LoadImage(Icon32Base64);
                createData.Image = LoadImage(Icon16Base64);
                if (panel.AddItem(createData) is PushButton createButton)
                    createButton.AvailabilityClassName = typeof(FlexV3Availability).FullName;
            }

            if (panel.GetItems().All(i => !string.Equals(i.Name, "RevitFlexConduit.EditToolsV301", StringComparison.OrdinalIgnoreCase)))
            {
                var editData = new PulldownButtonData("RevitFlexConduit.EditToolsV301", "Edit\nFlex")
                {
                    ToolTip = "Edit the selected Flex Conduit path, control points, connections, diameter, or convert it to native conduit.",
                    LargeImage = LoadImage(Icon32Base64),
                    Image = LoadImage(Icon16Base64)
                };
                PulldownButton? edit = panel.AddItem(editData) as PulldownButton;
                if (edit != null)
                {
                    AddTool(edit, assemblyPath, "Edit Path", typeof(FlexEditPathCommand));
                    AddTool(edit, assemblyPath, "Add Control Point", typeof(FlexAddPointCommand));
                    AddTool(edit, assemblyPath, "Delete Control Point", typeof(FlexDeletePointCommand));
                    AddTool(edit, assemblyPath, "Reset / Smooth Route", typeof(FlexSmoothCommand));
                    AddTool(edit, assemblyPath, "Reverse Direction", typeof(FlexReverseCommand));
                    AddTool(edit, assemblyPath, "Reconnect", typeof(FlexReconnectCommand));
                    AddTool(edit, assemblyPath, "Change Diameter", typeof(FlexDiameterCommand));
                    AddTool(edit, assemblyPath, "Convert to Conduit", typeof(FlexConvertCommand));
                }
            }

            TryMovePanelToSystemsAfterConduitFittings(PanelName);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show($"Flex Conduit v{ProductVersion}", "The Flex Conduit ribbon controls could not be created.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try { application.ControlledApplication.DocumentOpened -= OnDocumentOpened; } catch { }
        FlexConduitV3Updater.UnregisterApplicationUpdater();
        return Result.Succeeded;
    }

    private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try { FlexConduitV3Updater.RegisterExisting(e.Document); } catch { }
    }

    private static void AddTool(PulldownButton edit, string assemblyPath, string text, Type commandType)
    {
        var data = new PushButtonData("RevitFlexConduit301." + commandType.Name, text, assemblyPath, commandType.FullName!);
        PushButton button = edit.AddPushButton(data);
        button.AvailabilityClassName = typeof(FlexV3SelectedAvailability).FullName;
    }

    private static BitmapImage LoadImage(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool TryMovePanelToSystemsAfterConduitFittings(string ourPanelTitle)
    {
        try
        {
            Assembly? adWindows = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AdWindows", StringComparison.OrdinalIgnoreCase));
            adWindows ??= Assembly.Load("AdWindows");
            Type? componentManager = adWindows.GetType("Autodesk.Windows.ComponentManager");
            object? ribbon = componentManager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (ribbon == null) return false;

            var tabs = Enumerate(ribbon.GetType().GetProperty("Tabs")?.GetValue(ribbon)).ToList();
            object? systemsTab = tabs.FirstOrDefault(IsSystemsTab);
            if (systemsTab == null) return false;

            object? sourceTab = null;
            object? ourPanel = null;
            object? sourcePanels = null;
            foreach (object tab in tabs)
            {
                object? panels = tab.GetType().GetProperty("Panels")?.GetValue(tab);
                foreach (object candidate in Enumerate(panels))
                {
                    if (!GetPanelTitle(candidate).Equals(ourPanelTitle, StringComparison.OrdinalIgnoreCase)) continue;
                    sourceTab = tab;
                    ourPanel = candidate;
                    sourcePanels = panels;
                    break;
                }
                if (ourPanel != null) break;
            }

            if (ourPanel == null || sourcePanels == null) return false;
            object? targetPanelsObject = systemsTab.GetType().GetProperty("Panels")?.GetValue(systemsTab);
            if (targetPanelsObject == null) return false;
            var targetPanels = Enumerate(targetPanelsObject).ToList();
            int targetIndex = targetPanels.FindIndex(PanelContainsConduitFitting);
            if (targetIndex < 0)
                targetIndex = targetPanels.FindIndex(p => GetPanelTitle(p).Contains("Electrical", StringComparison.OrdinalIgnoreCase));

            bool alreadyOnSystems = ReferenceEquals(sourceTab, systemsTab);
            int currentIndex = targetPanels.IndexOf(ourPanel);
            if (alreadyOnSystems && targetIndex >= 0 && currentIndex == targetIndex + 1) return true;

            MethodInfo? remove = FindCollectionMethod(sourcePanels, "Remove", 1);
            MethodInfo? insert = FindCollectionMethod(targetPanelsObject, "Insert", 2);
            MethodInfo? add = FindCollectionMethod(targetPanelsObject, "Add", 1);
            if (remove == null || (insert == null && add == null)) return false;

            remove.Invoke(sourcePanels, new[] { ourPanel });
            targetPanels = Enumerate(targetPanelsObject).ToList();
            targetIndex = targetPanels.FindIndex(PanelContainsConduitFitting);
            if (targetIndex < 0)
                targetIndex = targetPanels.FindIndex(p => GetPanelTitle(p).Contains("Electrical", StringComparison.OrdinalIgnoreCase));

            if (insert != null)
            {
                int insertIndex = targetIndex >= 0 ? Math.Min(targetIndex + 1, targetPanels.Count) : targetPanels.Count;
                insert.Invoke(targetPanelsObject, new object[] { insertIndex, ourPanel });
            }
            else
            {
                add!.Invoke(targetPanelsObject, new[] { ourPanel });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindCollectionMethod(object collection, string name, int count)
        => collection.GetType().GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count);

    private static bool IsSystemsTab(object tab)
    {
        string text = string.Join(" ", new[]
        {
            TextProperty(tab, "Name"), TextProperty(tab, "Title"), TextProperty(tab, "Id"), TextProperty(tab, "AutomationName")
        });
        return text.Contains("Systems", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("System", StringComparison.OrdinalIgnoreCase) && !text.Contains("System Browser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PanelContainsConduitFitting(object panel)
    {
        object? source = panel.GetType().GetProperty("Source")?.GetValue(panel);
        object? items = source?.GetType().GetProperty("Items")?.GetValue(source);
        return Enumerate(items).Any(ItemContainsConduitFitting);
    }

    private static bool ItemContainsConduitFitting(object item)
    {
        string text = string.Join(" ", new[]
        {
            TextProperty(item, "Text"), TextProperty(item, "Name"), TextProperty(item, "Id"), TextProperty(item, "AutomationName")
        });
        if (text.Contains("Conduit Fitting", StringComparison.OrdinalIgnoreCase) || text.Contains("ConduitFitting", StringComparison.OrdinalIgnoreCase))
            return true;
        object? nested = item.GetType().GetProperty("Items")?.GetValue(item);
        return Enumerate(nested).Any(ItemContainsConduitFitting);
    }

    private static string GetPanelTitle(object panel)
    {
        object? source = panel.GetType().GetProperty("Source")?.GetValue(panel);
        string title = source == null ? string.Empty : TextProperty(source, "Title");
        return string.IsNullOrWhiteSpace(title) ? TextProperty(panel, "Title") : title;
    }

    private static string TextProperty(object obj, string property)
        => obj.GetType().GetProperty(property)?.GetValue(obj)?.ToString() ?? string.Empty;

    private static IEnumerable<object> Enumerate(object? source)
    {
        if (source is not IEnumerable enumerable) yield break;
        foreach (object? item in enumerable)
            if (item != null) yield return item;
    }
}
