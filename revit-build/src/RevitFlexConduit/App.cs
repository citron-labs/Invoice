using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitFlexConduit;

public sealed class App : IExternalApplication
{
    internal const string PanelName = "Flex Conduit";
    internal const string ProductVersion = "2.1.0";

    private const string Icon32Base64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAA9ElEQVR4nO2Wyw3DIAyGcdWR2nt2YIGMxgLZIfeyEz1BE2I7GFtBqvLfAtj+/CDCuVtKpdUljf3DCmQIgDZ7NYCFhgM8ew3Pyv+al7IfgwfqnEkFaphtcOzbHKA1mBkAVX4uOLVH9qY22vYRA3iHhXN1UPZHAmDE2aj1/p9BxeABBeBK+Zl9S2wH0y85yl8MHi75D3DX8LAhnWKsIjDhrcVmqulHFIMHKRjlp17bLXCDh52RZE+pzEBL8LxG9VQafAcgFTdYYoDW7DlBR/YFoEcWjxEVwFbQmX0BqMstKb8muEppdcmqDUODD38TdmlY6W/9pb5e8HLetjA3HgAAAABJRU5ErkJggg==";
    private const string Icon16Base64 = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAgklEQVR4nL1Syw2AIAx9JY6Ed3dgAUZzAXbgrjvVgykh5eOH6LuQtO+XFOBrcAT39mY0oGug060PRZvbDUSsTQqDWoqeWR9YZtQibt5Vm9ACynmpQS25JgaAfXXpnTRJljNOw1YT4VGeLkNBfgVJ1zBPqjfRMrn6hV1wBA8bvBb/igPTjT8qNv3lxAAAAABJRU5ErkJggg==";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RibbonPanel panel = application.GetRibbonPanels()
                .FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase))
                ?? application.CreateRibbonPanel(PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData(
                "RevitFlexConduit.Create",
                "Flex\nConduit",
                assemblyPath,
                typeof(FlexConduitCommand).FullName!);

            buttonData.ToolTip = $"Flex Conduit v{ProductVersion} — true spline routing with persistent editable control points.";
            buttonData.LongDescription = $"Revit Flex Conduit 2025 v{ProductVersion}. Create a smooth spline-based flex conduit. Control points remain visible after creation; select a control point and run Flex Conduit to move it and reshape the spline.";
            buttonData.LargeImage = LoadImage(Icon32Base64);
            buttonData.Image = LoadImage(Icon16Base64);

            if (panel.GetItems().All(i => !string.Equals(i.Name, "RevitFlexConduit.Create", StringComparison.OrdinalIgnoreCase)))
            {
                var item = panel.AddItem(buttonData) as PushButton;
                if (item != null)
                    item.AvailabilityClassName = typeof(FlexConduitAvailability).FullName;
            }

            TryMovePanelToSystemsAfterConduitFittings(PanelName);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show(
                $"Flex Conduit v{ProductVersion}",
                "The Flex Conduit ribbon control could not be created.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

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

            object? tabsObject = ribbon.GetType().GetProperty("Tabs")?.GetValue(ribbon);
            var tabs = Enumerate(tabsObject).ToList();
            if (tabs.Count == 0) return false;

            object? systemsTab = tabs.FirstOrDefault(IsSystemsTab);
            if (systemsTab == null) return false;

            object? sourceTab = null;
            object? ourPanel = null;
            object? sourcePanelsObject = null;

            foreach (object tab in tabs)
            {
                object? panelsObject = tab.GetType().GetProperty("Panels")?.GetValue(tab);
                foreach (object candidate in Enumerate(panelsObject))
                {
                    if (GetPanelTitle(candidate).Equals(ourPanelTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceTab = tab;
                        ourPanel = candidate;
                        sourcePanelsObject = panelsObject;
                        break;
                    }
                }

                if (ourPanel != null) break;
            }

            if (ourPanel == null || sourcePanelsObject == null) return false;

            object? systemsPanelsObject = systemsTab.GetType().GetProperty("Panels")?.GetValue(systemsTab);
            if (systemsPanelsObject == null) return false;

            var targetPanels = Enumerate(systemsPanelsObject).ToList();
            int targetIndex = targetPanels.FindIndex(PanelContainsConduitFitting);
            if (targetIndex < 0)
            {
                targetIndex = targetPanels.FindIndex(p =>
                    GetPanelTitle(p).Contains("Electrical", StringComparison.OrdinalIgnoreCase));
            }

            bool alreadyOnSystems = ReferenceEquals(sourceTab, systemsTab);
            int currentIndex = targetPanels.IndexOf(ourPanel);
            if (alreadyOnSystems && targetIndex >= 0 && currentIndex == targetIndex + 1)
                return true;

            MethodInfo? remove = FindCollectionMethod(sourcePanelsObject, "Remove", 1);
            MethodInfo? insert = FindCollectionMethod(systemsPanelsObject, "Insert", 2);
            MethodInfo? add = FindCollectionMethod(systemsPanelsObject, "Add", 1);
            if (remove == null || (insert == null && add == null)) return false;

            remove.Invoke(sourcePanelsObject, new[] { ourPanel });

            targetPanels = Enumerate(systemsPanelsObject).ToList();
            targetIndex = targetPanels.FindIndex(PanelContainsConduitFitting);
            if (targetIndex < 0)
                targetIndex = targetPanels.FindIndex(p => GetPanelTitle(p).Contains("Electrical", StringComparison.OrdinalIgnoreCase));

            if (insert != null)
            {
                int insertIndex = targetIndex >= 0 ? Math.Min(targetIndex + 1, targetPanels.Count) : targetPanels.Count;
                insert.Invoke(systemsPanelsObject, new object[] { insertIndex, ourPanel });
            }
            else
            {
                add!.Invoke(systemsPanelsObject, new[] { ourPanel });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindCollectionMethod(object collection, string name, int parameterCount)
        => collection.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);

    private static bool IsSystemsTab(object tab)
    {
        string identity = string.Join(" ", new[]
        {
            TextProperty(tab, "Name"),
            TextProperty(tab, "Title"),
            TextProperty(tab, "Id"),
            TextProperty(tab, "AutomationName")
        });

        return identity.Contains("Systems", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("System", StringComparison.OrdinalIgnoreCase) &&
               !identity.Contains("System Browser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PanelContainsConduitFitting(object panel)
    {
        object? source = panel.GetType().GetProperty("Source")?.GetValue(panel);
        if (source == null) return false;
        object? items = source.GetType().GetProperty("Items")?.GetValue(source);
        return Enumerate(items).Any(ItemContainsConduitFitting);
    }

    private static bool ItemContainsConduitFitting(object item)
    {
        string text = string.Join(" ", new[]
        {
            TextProperty(item, "Text"),
            TextProperty(item, "Name"),
            TextProperty(item, "Id"),
            TextProperty(item, "AutomationName")
        });

        if (text.Contains("Conduit Fitting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ConduitFitting", StringComparison.OrdinalIgnoreCase))
            return true;

        object? nested = item.GetType().GetProperty("Items")?.GetValue(item);
        return Enumerate(nested).Any(ItemContainsConduitFitting);
    }

    private static string GetPanelTitle(object panel)
    {
        object? source = panel.GetType().GetProperty("Source")?.GetValue(panel);
        if (source != null)
        {
            string title = TextProperty(source, "Title");
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }

        return TextProperty(panel, "Title");
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

public sealed class FlexConduitAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories)
        => applicationData.ActiveUIDocument?.Document != null;
}
