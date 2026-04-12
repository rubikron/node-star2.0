using System.Reflection;
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
    private readonly Action<string>? _diagnosticSink;

    /// <summary>
    /// Initializes the collector.
    /// </summary>
    /// <param name="diagnosticSink">
    /// Optional sink for non-fatal COM diagnostics. This is mainly useful in
    /// manual integration runs where swallowed interop failures need to be visible.
    /// </param>
    public SwStateCollector(Action<string>? diagnosticSink = null)
    {
        _diagnosticSink = diagnosticSink;
    }

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

        var activeDocumentCom       = GetActiveDocument(app);
        var documents               = CollectOpenDocuments(app);
        var activeDetail            = TryCollectDocument(activeDocumentCom);
        var activeDocumentState     = ToDocumentState(activeDetail);
        var activeConfigurationName = TryGetActiveConfigurationName(activeDocumentCom);
        var selection               = CollectSelection(activeDocumentState, activeDocumentCom);

        // Ensure the active document is present in the open-documents list
        if (activeDetail is not null && documents.All(doc => !string.Equals(doc.Id, activeDetail.Id, StringComparison.Ordinal)))
        {
            documents = documents.Append(activeDetail)
                .OrderBy(static doc => doc.Id, StringComparer.Ordinal)
                .ToArray();
        }

        return SwState.Create(
            connector.RevisionNumber,
            activeDocumentState,
            documents,
            selection,
            activeConfigurationName);
    }

    private static SwDocumentState? ToDocumentState(SwDocumentDetail? detail) =>
        detail is null ? null
            : new SwDocumentState(detail.Id, detail.Title, detail.Path, detail.DocumentType, detail.ConfigurationName);

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

    private IReadOnlyList<SwDocumentDetail> CollectOpenDocuments(ISldWorks app)
    {
        var documents = new List<SwDocumentDetail>();
        var current = SafeGet(
            "ISldWorks.IGetFirstDocument2",
            () => app.IGetFirstDocument2());

        while (current is not null)
        {
            var document = TryCollectDocument(current);
            if (document is not null)
                documents.Add(document);

            current = SafeGet(
                "IModelDoc2.IGetNext",
                () => current.IGetNext());
        }

        return documents
            .DistinctBy(static doc => doc.Id, StringComparer.Ordinal)
            .OrderBy(static doc => doc.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private IModelDoc2? GetActiveDocument(ISldWorks app) =>
        SafeGet(
            "ISldWorks.IActiveDoc2",
            () => app.IActiveDoc2);

    private IReadOnlyList<SwSelectionState> CollectSelection(
        SwDocumentState? activeDocument,
        IModelDoc2? activeDocumentCom)
    {
        if (activeDocument is null || activeDocumentCom is null)
        {
            return [];
        }

        var selectionManager = SafeGet(
            "IModelDoc2.SelectionManager",
            () => activeDocumentCom.SelectionManager as ISelectionMgr);

        if (selectionManager is null)
        {
            return [];
        }

        var count = SafeGetInt(
            "ISelectionMgr.GetSelectedObjectCount2",
            () => selectionManager.GetSelectedObjectCount2(-1));

        if (count <= 0)
        {
            return [];
        }

        var selection = new List<SwSelectionState>(count);

        for (var index = 1; index <= count; index++)
        {
            var selectedObject = SafeGet(
                $"ISelectionMgr.GetSelectedObject6[{index}]",
                () => selectionManager.GetSelectedObject6(index, -1));
            var selectionType = SafeGetInt(
                $"ISelectionMgr.GetSelectedObjectType3[{index}]",
                () => selectionManager.GetSelectedObjectType3(index, -1));
            var mark = SafeGetNullableInt(
                $"ISelectionMgr.GetSelectedObjectMark[{index}]",
                () => selectionManager.GetSelectedObjectMark(index));

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

    private SwDocumentDetail? TryCollectDocument(IModelDoc2? document)
    {
        if (document is null)
            return null;

        var path          = Normalize(SafeGetString("IModelDoc2.GetPathName", document.GetPathName));
        var title         = Normalize(SafeGetString("IModelDoc2.GetTitle",    document.GetTitle));
        var typeCode      = MapDocumentType(SafeGetInt("IModelDoc2.GetType",  document.GetType));
        var configuration = TryGetActiveConfigurationName(document);
        var runtimeToken  = BuildRuntimeToken(document, title, typeCode);
        var identity      = BuildDocumentIdentity(path, runtimeToken);

        // Model data fields are empty here — GetSwStateTool enriches them after collection.
        return new SwDocumentDetail
        {
            Id                = identity,
            Title             = title,
            Path              = path,
            DocumentType      = typeCode,
            ConfigurationName = configuration,
            ModelSnapshot     = string.Empty
        };
    }

    private string? TryGetActiveConfigurationName(IModelDoc2? document)
    {
        if (document is null)
        {
            return null;
        }

        var configurationManager = SafeGet(
            "IModelDoc2.ConfigurationManager",
            () => document.ConfigurationManager as IConfigurationManager);
        var activeConfiguration = configurationManager is null
            ? null
            : SafeGet(
                "IConfigurationManager.ActiveConfiguration",
                () => configurationManager.ActiveConfiguration);

        return Normalize(
            activeConfiguration is null
                ? null
                : SafeGetString(
                    "IConfiguration.Name",
                    () => activeConfiguration.Name));
    }

    private static string BuildRuntimeToken(IModelDoc2 document, string? title, string documentType)
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

    private string TryGetBestObjectName(object? selectedObject)
    {
        if (selectedObject is null)
        {
            return string.Empty;
        }

        foreach (var candidate in GetCandidateObjectNames(selectedObject))
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }
        }

        return selectedObject.GetType().Name;
    }

    private IEnumerable<string?> GetCandidateObjectNames(object selectedObject)
    {
        yield return TryReadDynamicString(selectedObject, "Name");
        yield return TryReadDynamicString(selectedObject, "Name2");
        yield return TryInvokeDynamicString(selectedObject, "GetName");
        yield return TryInvokeDynamicString(selectedObject, "GetName2");
        yield return TryInvokeDynamicString(selectedObject, "GetPathName");
        yield return TryInvokeDynamicString(selectedObject, "GetTitle");
    }

    private string? TryReadDynamicString(object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName);
            return Normalize(property?.GetValue(instance)?.ToString());
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException comEx)
        {
            ReportComException($"{instance.GetType().Name}.{propertyName}", comEx);
            return null;
        }
        catch (COMException ex)
        {
            ReportComException($"{instance.GetType().Name}.{propertyName}", ex);
            return null;
        }
    }

    private string? TryInvokeDynamicString(object instance, string methodName)
    {
        try
        {
            var method = instance.GetType().GetMethod(methodName, Type.EmptyTypes);
            return Normalize(method?.Invoke(instance, null)?.ToString());
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException comEx)
        {
            ReportComException($"{instance.GetType().Name}.{methodName}()", comEx);
            return null;
        }
        catch (COMException ex)
        {
            ReportComException($"{instance.GetType().Name}.{methodName}()", ex);
            return null;
        }
    }

    private T? SafeGet<T>(string operation, Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (COMException ex)
        {
            ReportComException(operation, ex);
            return default;
        }
        catch (InvalidCastException ex)
        {
            _diagnosticSink?.Invoke($"SW state cast failure in {operation}: {ex.Message}");
            return default;
        }
    }

    private string? SafeGetString(string operation, Func<string> getter) =>
        Normalize(SafeGet(operation, getter));

    private int SafeGetInt(string operation, Func<int> getter) =>
        SafeGet(operation, getter);

    private int? SafeGetNullableInt(string operation, Func<int> getter) =>
        SafeGet(operation, getter);

    private void ReportComException(string operation, COMException ex)
    {
        _diagnosticSink?.Invoke(
            $"COM failure in {operation}: 0x{ex.HResult:x8} {ex.Message}");
    }

    private static string MapDocumentType(int documentType) =>
        documentType switch
        {
            (int)swDocumentTypes_e.swDocPART => "prt",
            (int)swDocumentTypes_e.swDocASSEMBLY => "asm",
            (int)swDocumentTypes_e.swDocDRAWING => "drw",
            _ => $"doc:{documentType}"
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
