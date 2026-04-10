#nullable enable
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class ApprovalService
{
    public async Task<ApprovalDecision> RequestApprovalAsync(
        DeviceModel? device,
        string approvalMode,
        bool isReconnect,
        CancellationToken cancellationToken)
    {
        if (approvalMode == "Pre-approved device")
        {
            // 사전 승인 장치는 명시적으로 신뢰 장치로 표시된 경우에만 허용합니다.
            bool isPreApproved = device is not null && device.IsFavorite;
            return isPreApproved ? ApprovalDecision.Approved : ApprovalDecision.Denied;
        }

        if (isReconnect)
        {
            return ApprovalDecision.Approved;
        }

        string title = approvalMode == "Support request" ? "지원 세션 승인 요청" : "원격 연결 승인 요청";
        string targetName = device?.Name ?? "Unknown Device";
        string message = approvalMode == "Support request"
            ? $"'{targetName}' 장치에 대한 지원 요청 세션을 승인하시겠습니까?"
            : $"'{targetName}' 장치에 대한 원격 연결을 승인하시겠습니까?";

        MessageBoxResult result = await Application.Current.Dispatcher.InvokeAsync(
            () => MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question)).Task.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            MessageBoxResult.Yes => ApprovalDecision.Approved,
            MessageBoxResult.No => ApprovalDecision.Denied,
            _ => ApprovalDecision.Cancelled
        };
    }
}
