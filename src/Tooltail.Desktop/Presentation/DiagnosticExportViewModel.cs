using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Tooltail.Desktop.Presentation;

public sealed class DiagnosticExportViewModel : INotifyPropertyChanged
{
    private bool isBusy;
    private string reasonCode = "diagnostic.ready";
    private string status =
        "Preview a closed, redacted diagnostic summary before exporting it.";
    private string previewJson = string.Empty;
    private bool hasExportablePreview;
    private int byteCount;
    private string sha256 = "No diagnostic preview.";
    private string exportPath = "No diagnostic export.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy => isBusy;

    public bool CanPreview => !isBusy;

    public bool CanExport => !isBusy && hasExportablePreview;

    public bool HasPreview => previewJson.Length > 0;

    public string ReasonCode => reasonCode;

    public string Status => status;

    public string PreviewJson => previewJson;

    public int ByteCount => byteCount;

    public string Sha256 => sha256;

    public string ExportPath => exportPath;

    public void Begin(string reasonCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        isBusy = true;
        this.reasonCode = reasonCode;
        status = message;
        NotifyAll();
    }

    public void ApplyPreview(DiagnosticPreviewWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        isBusy = false;
        reasonCode = result.ReasonCode;
        if (result.IsSuccess && result.Sha256 is not null)
        {
            previewJson = result.PreviewJson;
            hasExportablePreview = true;
            byteCount = result.ByteCount;
            sha256 = result.Sha256;
            status =
                "Exact redacted preview ready. Review it before creating a new local export.";
        }
        else
        {
            previewJson = string.Empty;
            hasExportablePreview = false;
            byteCount = 0;
            sha256 = "No valid diagnostic preview.";
            status = "Diagnostic preview failed closed; no export was created.";
        }

        NotifyAll();
    }

    public void ApplyExport(DiagnosticExportWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        isBusy = false;
        reasonCode = result.ReasonCode;
        if (result.IsSuccess && result.CanonicalPath is not null)
        {
            hasExportablePreview = false;
            exportPath = result.CanonicalPath;
            byteCount = result.ByteCount;
            sha256 = result.Sha256!;
            status = "The exact reviewed redacted diagnostic was exported locally.";
        }
        else
        {
            status = "Diagnostic export failed; the reviewed preview remains visible.";
        }

        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ReasonCode));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(PreviewJson));
        OnPropertyChanged(nameof(ByteCount));
        OnPropertyChanged(nameof(Sha256));
        OnPropertyChanged(nameof(ExportPath));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
