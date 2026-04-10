using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwBridge.Connection;
using SwBridge.Models;

namespace SwBridge.Tools.Session;

/// <summary>
/// Collects a unified SOLIDWORKS session snapshot from the live COM application object.
/// </summary>
public sealed class SwStateCollector
{
    /// <summary>
    /// Builds a compact state snapshot from the current SOLIDWORKS session.
    /// </summary>
    /// <param name="connector">Connected SOLIDWORKS connector.</param>
    /// <returns>The current session snapshot.</returns>
    public SwState Collect(ISwConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        var app = connector.Application
            ?? throw new InvalidOperationException("SOLIDWORKS is not connected.");

        var activeDocumentCom = GetActiveDocument(app);
        var documents = CollectOpenDocuments(app);
        var activeDocument = TryCollectDocument(activeDocumentCom);
        var activeConfigurationName = TryGetActiveConfigurationName(activeDocumentCom);
        var selection = CollectSelection(activeDocument, activeDocumentCom);

        if (activeDocument is not null && documents.All(doc => !string.Equals(doc.Id, activeDocument.Id, StringComparison.Ordinal)))
        {
            documents = documents.Append(activeDocument)
                .OrderBy(static doc => doc.Id, StringComparer.Ordinal)
                .ToArray();
        }

        return SwState.Create(
            connector.RevisionNumber,
            activeDocument,
            documents,
            selection,
            activeConfigurationName);
    }

    /// <summary>
    /// Builds a stable document identity from the best available information.
    /// </summary>
    /// <param name="path">Saved document path when available.</param>
    /// <param name="fallbackToken">Runtime session token used for unsaved documents.</param>
    /// <returns>A stable document identity for diffing.</returns>
    internal static string BuildDocumentIdentity(string? path, string fallbackToken)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }

        return $"unsaved:{fallbackToken}";
    }

    private static IReadOnlyList<SwDocumentState> CollectOpenDocuments(ISldWorks app)
    {
        var documents = new List<SwDocumentState>();
        dynamic dynamicApp = app;

        foreach (var candidate in EnumerateComArray(SafeGet(() => dynamicApp.GetDocuments())))
        {
            var document = TryCollectDocument(candidate);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        return documents
            .OrderBy(static doc => doc.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static object? GetActiveDocument(ISldWorks app)
    {
        dynamic dynamicApp = app;
        return SafeGet(() => dynamicApp.ActiveDoc) ?? SafeGet(() => dynamicApp.IActiveDoc2);
    }

    private static IReadOnlyList<SwSelectionState> CollectSelection(SwDocumentState? activeDocument, object? activeDocumentCom)
    {
        if (activeDocument is null || activeDocumentCom is null)
        {
            return [];
        }

        dynamic doc = activeDocumentCom;
        var selectionManager = SafeGet(() => doc.SelectionManager);
        if (selectionManager is null)
        {
            return [];
        }

        dynamic dynamicSelectionManager = selectionManager;
        var count = SafeGetInt(() => dynamicSelectionManager.GetSelectedObjectCount2(-1));
        if (count <= 0)
        {
            return [];
        }

        var selection = new List<SwSelectionState>(count);

        for (var index = 1; index <= count; index++)
        {
            var selectedObject = SafeGet(() => dynamicSelectionManager.GetSelectedObject6(index, -1));
            var selectionType = SafeGetInt(() => dynamicSelectionManager.GetSelectedObjectType3(index, -1));
            var mark = SafeGetNullableInt(() => dynamicSelectionManager.GetSelectedObjectMark(index));
            var typeCode = MapSelectionType(selectionType);
            var name = TryGetBestObjectName(selectedObject);
            var keyName = string.IsNullOrWhiteSpace(name)
                ? $"idx:{index}"
                : name.Trim();

            selection.Add(
                new SwSelectionState(
                    $"{activeDocument.Id}|{typeCode}|{keyName}",
                    activeDocument.Id,
                    typeCode,
                    Normalize(name),
                    mark));
        }

        return selection
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static SwDocumentState? TryCollectDocument(object? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        dynamic document = candidate;

        var path = Normalize(SafeGetString(() => document.GetPathName()));
        var title = Normalize(SafeGetString(() => document.GetTitle()));
        var typeCode = MapDocumentType(SafeGetInt(() => document.GetType()));
        var configuration = TryGetActiveConfigurationName(candidate);
        var runtimeToken = BuildRuntimeToken(candidate, title, typeCode);
        var identity = BuildDocumentIdentity(path, runtimeToken);

        return new SwDocumentState(identity, title, path, typeCode, configuration);
    }

    private static string? TryGetActiveConfigurationName(object? document)
    {
        if (document is null)
        {
            return null;
        }

        dynamic dynamicDocument = document;
        var configurationManager = SafeGet(() => dynamicDocument.ConfigurationManager);
        var activeConfiguration = configurationManager is null
            ? null
            : SafeGet(() => ((dynamic)configurationManager).ActiveConfiguration);

        return Normalize(
            activeConfiguration is null
                ? null
                : SafeGetString(() => ((dynamic)activeConfiguration).Name));
    }

    private static string BuildRuntimeToken(object document, string? title, string documentType)
    {
        try
        {
            var unknown = Marshal.GetIUnknownForObject(document);
            try
            {
                return $"ptr:{unknown.ToInt64():x}";
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (COMException)
        {
            return $"{documentType}:{Normalize(title) ?? "untitled"}";
        }
    }

    private static string TryGetBestObjectName(object? selectedObject)
    {
        if (selectedObject is null)
        {
            return string.Empty;
        }

        dynamic dynamicObject = selectedObject;

        foreach (var getter in CandidateNameGetters(dynamicObject))
        {
            var value = Normalize(getter());
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return selectedObject.GetType().Name;
    }

    private static IEnumerable<Func<string?>> CandidateNameGetters(dynamic dynamicObject)
    {
        yield return () => SafeGetString(() => dynamicObject.Name);
        yield return () => SafeGetString(() => dynamicObject.Name2);
        yield return () => SafeGetString(() => dynamicObject.GetName());
        yield return () => SafeGetString(() => dynamicObject.GetName2());
        yield return () => SafeGetString(() => dynamicObject.GetPathName());
        yield return () => SafeGetString(() => dynamicObject.GetTitle());
    }

    private static IEnumerable<object> EnumerateComArray(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case Array array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
                }

                yield break;
            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
                }

                yield break;
            default:
                yield return value;
                break;
        }
    }

    private static object? SafeGet(Func<object?> getter)
    {
        try
        {
            return getter();
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static string? SafeGetString(Func<object?> getter) =>
        SafeGet(getter)?.ToString();

    private static int SafeGetInt(Func<object?> getter)
    {
        var value = SafeGet(getter);
        return value switch
        {
            int integer => integer,
            short shortValue => shortValue,
            long longValue => (int)longValue,
            _ => 0
        };
    }

    private static int? SafeGetNullableInt(Func<object?> getter)
    {
        var value = SafeGet(getter);
        return value switch
        {
            null => null,
            int integer => integer,
            short shortValue => shortValue,
            long longValue => (int)longValue,
            _ => null
        };
    }

    private static string MapDocumentType(int documentType) =>
        documentType switch
        {
            (int)swDocumentTypes_e.swDocPART => "prt",
            (int)swDocumentTypes_e.swDocASSEMBLY => "asm",
            (int)swDocumentTypes_e.swDocDRAWING => "drw",
            _ => "unk"
        };

    private static string MapSelectionType(int selectionType)
    {
        var enumName = Enum.GetName(typeof(swSelectType_e), selectionType);
        if (string.IsNullOrWhiteSpace(enumName))
        {
            return $"sel:{selectionType}";
        }

        return enumName
            .Replace("swSel", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
