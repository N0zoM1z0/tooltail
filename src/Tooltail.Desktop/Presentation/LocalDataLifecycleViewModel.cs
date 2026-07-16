using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed class LocalDataLifecycleViewModel : INotifyPropertyChanged
{
    public const string RequiredConfirmation = "DELETE LOCAL STATE";

    private bool isBusy;
    private string reasonCode = "local_state.ready";
    private string status =
        "Prepare a deletion preview to inspect the exact local memory boundary.";
    private string confirmationText = string.Empty;
    private Guid? requestId;
    private DateTimeOffset? expiresUtc;
    private IReadOnlyList<string> deletedCategories = [];
    private IReadOnlyList<string> preservedCategories = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy => isBusy;

    public bool CanPrepare => !isBusy;

    public bool CanDelete =>
        !isBusy &&
        requestId is not null &&
        string.Equals(
            confirmationText,
            RequiredConfirmation,
            StringComparison.Ordinal);

    public bool HasPreview => requestId is not null;

    public string ReasonCode => reasonCode;

    public string Status => status;

    public string RequiredConfirmationText { get; } = RequiredConfirmation;

    public string ConfirmationText
    {
        get => confirmationText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(confirmationText, value, StringComparison.Ordinal))
            {
                return;
            }

            confirmationText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDelete));
        }
    }

    public string ExpiresUtc => expiresUtc?.ToString("O") ?? "No active deletion preview.";

    public IReadOnlyList<string> DeletedCategories => deletedCategories;

    public IReadOnlyList<string> PreservedCategories => preservedCategories;

    public void ApplyPreview(LocalStateDeletionPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        isBusy = false;
        reasonCode = preview.ReasonCode;
        requestId = preview.IsSuccess ? preview.RequestId : null;
        expiresUtc = preview.IsSuccess ? preview.ExpiresUtc : null;
        deletedCategories = preview.DeletedCategories;
        preservedCategories = preview.PreservedCategories;
        confirmationText = string.Empty;
        status = preview.IsSuccess
            ? "Preview ready. Review both lists, then type the exact confirmation phrase."
            : "Deletion preview is unavailable; no local product state was changed.";
        NotifyAll();
    }

    public bool TryBeginDeletion(out Guid authorizedRequestId)
    {
        if (!CanDelete || requestId is not Guid value)
        {
            authorizedRequestId = Guid.Empty;
            return false;
        }

        isBusy = true;
        reasonCode = "local_state.deleting";
        status = "Deleting only the reviewed Tooltail-owned local state boundary…";
        authorizedRequestId = value;
        NotifyAll();
        return true;
    }

    public void ApplyFailure(string failureReasonCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        isBusy = false;
        reasonCode = failureReasonCode;
        status = message;
        NotifyAll();
    }

    public void ApplyResult(LocalStateDeletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        isBusy = false;
        reasonCode = result.ReasonCode;
        status = result.IsSuccess
            ? "Local product memory was deleted. Tooltail must now close; the next launch starts with fresh state."
            : result.RequiresRecovery
                ? "Deletion became incomplete. Tooltail must close and validate the recovery intent before opening state again."
                : "Local product memory was not deleted.";
        requestId = null;
        expiresUtc = null;
        confirmationText = string.Empty;
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ReasonCode));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ConfirmationText));
        OnPropertyChanged(nameof(ExpiresUtc));
        OnPropertyChanged(nameof(DeletedCategories));
        OnPropertyChanged(nameof(PreservedCategories));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
