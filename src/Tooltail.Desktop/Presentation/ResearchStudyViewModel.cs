using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tooltail.Infrastructure.LocalResearch;

namespace Tooltail.Desktop.Presentation;

public sealed class ResearchStudyViewModel : INotifyPropertyChanged
{
    private bool initialized;
    private bool isHealthy = true;
    private bool isBusy;
    private bool isEnabled;
    private string reasonCode = "research.not_initialized";
    private string previewJsonl = string.Empty;
    private string exportPath = "No research export has been created.";
    private Guid? studyId;
    private Guid? sessionId;
    private int eventCount;
    private long eventBytes;
    private int? selectedRating;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<int> RatingChoices { get; } = [1, 2, 3, 4, 5, 6, 7];

    public bool IsEnabled => isEnabled;

    public bool IsBusy => isBusy;

    public bool CanEnable => initialized && !isBusy && !isEnabled;

    public bool CanPreview => initialized && !isBusy;

    public bool CanExport => initialized && !isBusy && eventCount > 0;

    public bool CanDelete => initialized && !isBusy;

    public bool CanResetSession => initialized && !isBusy && isEnabled;

    public bool CanSubmitRating =>
        initialized && !isBusy && isEnabled && selectedRating is >= 1 and <= 7;

    public string Status => !isHealthy
        ? "ERROR — inspect or delete local study data; research never controls product work"
        : isEnabled
            ? "ON — closed local study events may be recorded"
            : "OFF — no workflow study events are recorded";

    public string ReasonCode => reasonCode;

    public string StudyId => studyId?.ToString("D") ?? "none";

    public string SessionId => sessionId?.ToString("D") ?? "none";

    public int EventCount => eventCount;

    public long EventBytes => eventBytes;

    public string PreviewJsonl => previewJsonl;

    public bool HasPreview => previewJsonl.Length > 0;

    public string ExportPath => exportPath;

    public int? SelectedRating
    {
        get => selectedRating;
        set
        {
            if (selectedRating == value)
            {
                return;
            }

            selectedRating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmitRating));
        }
    }

    public void Begin(string reason)
    {
        isBusy = true;
        reasonCode = reason;
        NotifyState();
    }

    public void ApplyStatus(ResearchStoreStatus status)
    {
        initialized = true;
        isHealthy = status.IsSuccess;
        isBusy = false;
        isEnabled = status.IsEnabled;
        reasonCode = status.ReasonCode;
        studyId = status.StudyId;
        sessionId = status.SessionId;
        eventCount = status.EventCount;
        eventBytes = status.EventBytes;
        if (!status.IsEnabled)
        {
            selectedRating = null;
        }

        NotifyState();
    }

    public void ApplyPreview(ResearchPreviewResult result)
    {
        isBusy = false;
        isHealthy = result.IsSuccess;
        reasonCode = result.ReasonCode;
        if (result.IsSuccess)
        {
            isHealthy = true;
            previewJsonl = result.PreviewJsonl;
            eventCount = result.EventCount;
            eventBytes = result.ByteCount;
        }

        NotifyState();
    }

    public void ApplyExport(ResearchExportResult result)
    {
        isBusy = false;
        isHealthy = result.IsSuccess;
        reasonCode = result.ReasonCode;
        if (result.IsSuccess && result.CanonicalPath is not null)
        {
            isHealthy = true;
            exportPath = result.CanonicalPath;
            eventCount = result.EventCount;
            eventBytes = result.ByteCount;
        }

        NotifyState();
    }

    public void ApplyWrite(ResearchWriteResult result)
    {
        if (result.IsSuccess)
        {
            eventCount++;
            reasonCode = result.ReasonCode;
        }
        else if (result.ReasonCode != "research.consent_required")
        {
            isHealthy = false;
            reasonCode = result.ReasonCode;
        }

        NotifyState();
    }

    public void ClearSubmittedRating()
    {
        selectedRating = null;
        OnPropertyChanged(nameof(SelectedRating));
        OnPropertyChanged(nameof(CanSubmitRating));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanResetSession));
        OnPropertyChanged(nameof(CanSubmitRating));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ReasonCode));
        OnPropertyChanged(nameof(StudyId));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(EventCount));
        OnPropertyChanged(nameof(EventBytes));
        OnPropertyChanged(nameof(PreviewJsonl));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ExportPath));
        OnPropertyChanged(nameof(SelectedRating));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
