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
    internal const string TabName = "Systems";
    internal const string PanelName = "Flex Conduit";

    private const string Icon32Base64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAA9ElEQVR4nO2Wyw3DIAyGcdWR2nt2YIGMxgLZIfeyEz1BE2I7GFtBqvLfAtj+/CDCuVtKpdUljf3DCmQIgDZ7NYCFhgM8ew3Pyv+al7IfgwfqnEkFaphtcOzbHKA1mBkAVX4uOLVH9qY22vYRA3iHhXN1UPZHAmDE2aj1/p9BxeABBeBK+Zl9S2wH0y85yl8MHi75D3DX8LAhnWKsIjDhrcVmqulHFIMHKRjlp17bLXCDh52RZE+pzEBL8LxG9VQafAcgFTdYYoDW7DlBR/YFoEcWjxEVwFbQmX0BqMstKb8muEppdcmqDUODD38TdmlY6W/9pb5e8HLetjA3HgAAAABJRU5ErkJggg==";
    private const string Icon16Base64 = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAgklEQVR4nL1Syw2AIAx9JY6Ed3dgAUZzAXbgrjvVgykh5eOH6LuQtO+XFOBrcAT39mY0oGug060PRZvbDUSsTQqDWoqeWR9YZtQibt5Vm9ACynmpQS25JgaAfXXpnTRJljNOw1YT4VGeLkNBfgVJ1zBPqjfRMrn6hV1wBA8bvBb/igPTjT8qNv3lxAAAAABJRU5ErkJggg==";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RibbonPanel panel;
            try
            {
                panel = application.GetRibbonPanels(TabName)
                    .FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(TabName, PanelName);
            }
            catch
            {
                // English Revit 2025 uses the built-in Systems tab. If Autodesk changes/localizes
                // the tab identifier, keep the add-in available rather than failing startup.
                const string fallbackTab = "Electrical Tools";
                try { application.CreateRibbonTab(fallbackTab); } catch { }
                panel = application.GetRibbonPanels(fallbackTab)
                    .FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(fallbackTab, PanelName);
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData(
                "RevitFlexConduit.Create",
                "Flex\nConduit",
                assemblyPath,
                typeof(FlexConduitCommand).FullName!);

            buttonData.ToolTip = "Create or reshape an electrical flex-conduit run with visible control points and automatic smooth routing.";
            buttonData.LongDescription = "Pick a start point and additional control points. The conduit is created and refreshed while you work, so the route stays visible. Select an existing Flex Conduit run and launch the same command to reshape it.";
            buttonData.LargeImage = LoadImage(Icon32Base64);
            buttonData.Image = LoadImage(Icon16Base64);

            var item = panel.AddItem(buttonData) as PushButton;
            if (item != null)
                item.AvailabilityClassName = typeof(FlexConduitAvailability).FullName;

            // The supported Revit API can add a custom panel to Systems, but cannot insert an
            // add-in button directly into Autodesk's built-in Electrical panel. Reorder our panel
            // through Autodesk's loaded ribbon object so it sits immediately after the panel that
            // contains Conduit Fitting(s). If this internal UI changes, the panel simply stays on Systems.
            TryPlacePanelAfterConduitFittings(PanelName);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Flex Conduit", "The ribbon control could not be created.\n\n" + ex.Message);
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

    private static void TryPlacePanelAfterConduitFittings(string ourPanelTitle)
    {
        try
        {
            Assembly? adWindows = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "AdWindows", StringComparison.OrdinalIgnoreCase));
            adWindows ??= Assembly.Load("AdWindows");

            Type? componentManager = adWindows.GetType("Autodesk.Windows.ComponentManager");
            object? ribbon = componentManager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (ribbon == null) return;

            object? tabsObject = ribbon.GetType().GetProperty("Tabs")?.GetValue(ribbon);
            var tabs = Enumerate(tabsObject).ToList();
            object? systems = tabs.FirstOrDefault(t =>
                TextProperty(t, "Name").Equals("Systems", StringComparison.OrdinalIgnoreCase) ||
                TextProperty(t, "Title").Equals("Systems", StringComparison.OrdinalIgnoreCase) ||
                TextProperty(t, "Id").Contains("Systems", StringComparison.OrdinalIgnoreCase));
            if (systems == null) return;

            object? panelsObject = systems.GetType().GetProperty("Panels")?.GetValue(systems);
            if (panelsObject == null) return;
            var panels = Enumerate(panelsObject).ToList();
            object? ours = panels.FirstOrDefault(p => GetPanelTitle(p).Equals(ourPanelTitle, StringComparison.OrdinalIgnoreCase));
            if (ours == null) return;

            int targetIndex = panels.FindIndex(PanelContainsConduitFitting);
            int ourIndex = panels.IndexOf(ours);
            if (targetIndex < 0 || ourIndex < 0 || ourIndex == targetIndex + 1) return;

            MethodInfo? remove = panelsObject.GetType().GetMethod("Remove", new[] { ours.GetType() });
            remove ??= panelsObject.GetType().GetMethods().FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);
            MethodInfo? insert = panelsObject.GetType().GetMethods().FirstOrDefault(m => m.Name == "Insert" && m.GetParameters().Length == 2);
            if (remove == null || insert == null) return;

            remove.Invoke(panelsObject, new[] { ours });
            panels = Enumerate(panelsObject).ToList();
            targetIndex = panels.FindIndex(PanelContainsConduitFitting);
            int insertIndex = targetIndex >= 0 ? targetIndex + 1 : panels.Count;
            insert.Invoke(panelsObject, new object[] { insertIndex, ours });
        }
        catch
        {
            // UI reordering uses Autodesk's internal ribbon object. Failure must never stop Revit.
        }
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
        return source == null ? string.Empty : TextProperty(source, "Title");
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
