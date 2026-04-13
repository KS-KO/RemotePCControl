#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

internal enum ApprovalPolicy
{
    UserApproval,
    PreApprovedDevice,
    SupportRequest
}

public sealed class ApprovalService
{
    private readonly DevicePreferenceStore _devicePreferenceStore = new();

    public async Task<ApprovalDecision> RequestApprovalAsync(
        DeviceModel? device,
        string approvalMode,
        bool isReconnect,
        CancellationToken cancellationToken)
    {
        ApprovalPolicy policy = ApprovalPolicyParser.Parse(approvalMode);

        if (policy == ApprovalPolicy.PreApprovedDevice)
        {
            // 앱 재시작 이후에도 정책이 일관되도록 메모리 상태와 영속 저장소를 함께 확인합니다.
            bool isPreApproved = device is not null
                && (device.IsFavorite || _devicePreferenceStore.IsFavoriteDevice(device.InternalGuid));
            return isPreApproved ? ApprovalDecision.Approved : ApprovalDecision.Denied;
        }

        if (isReconnect)
        {
            return ApprovalDecision.Approved;
        }

        ApprovalPromptOptions promptOptions = ApprovalPromptOptions.Create(policy, device?.Name ?? "Unknown Device");
        return await ApprovalPromptWindow
            .ShowAsync(promptOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static string NormalizePolicy(string approvalMode) => ApprovalPolicyParser.Parse(approvalMode).ToDisplayText();

    public static bool RequiresInteractiveApproval(string approvalMode) => ApprovalPolicyParser.Parse(approvalMode).RequiresInteractiveApproval();

    public static string GetApprovalLogCategory(string approvalMode) => ApprovalPolicyParser.Parse(approvalMode).GetLogCategory();

    private static class ApprovalPolicyParser
    {
        public static ApprovalPolicy Parse(string? approvalMode)
        {
            return approvalMode switch
            {
                "Pre-approved device" => ApprovalPolicy.PreApprovedDevice,
                "Support request" => ApprovalPolicy.SupportRequest,
                _ => ApprovalPolicy.UserApproval
            };
        }
    }

    private sealed record ApprovalPromptOptions(
        string Title,
        string Headline,
        string Message,
        string ApproveButtonText,
        string DenyButtonText,
        string CancelButtonText,
        TimeSpan Timeout)
    {
        public static ApprovalPromptOptions Create(ApprovalPolicy policy, string targetName)
        {
            return policy switch
            {
                ApprovalPolicy.SupportRequest => new ApprovalPromptOptions(
                    "지원 세션 승인 요청",
                    "지원 세션 요청",
                    $"'{targetName}' 장치에 대한 지원 세션 요청이 들어왔습니다. 지원 세션을 허용하시겠습니까?",
                    "지원 허용",
                    "지원 거부",
                    "나중에",
                    TimeSpan.FromSeconds(90)),
                _ => new ApprovalPromptOptions(
                    "원격 연결 승인 요청",
                    "원격 연결 요청",
                    $"'{targetName}' 장치에 대한 원격 연결 요청이 들어왔습니다. 연결을 허용하시겠습니까?",
                    "허용",
                    "거부",
                    "취소",
                    TimeSpan.FromSeconds(60))
            };
        }
    }

    private sealed class ApprovalPromptWindow : Window
    {
        private readonly DispatcherTimer _timeoutTimer;
        private readonly TextBlock _countdownText;
        private readonly DateTime _expiresAtUtc;
        private ApprovalDecision _decision = ApprovalDecision.Cancelled;

        private ApprovalPromptWindow(ApprovalPromptOptions options)
        {
            Title = options.Title;
            Width = 420;
            Height = 260;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = System.Windows.Media.Brushes.White;
            ShowInTaskbar = false;

            _expiresAtUtc = DateTime.UtcNow.Add(options.Timeout);
            _countdownText = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184))
            };

            System.Windows.Controls.Button approveButton = CreateButton(options.ApproveButtonText, System.Windows.Media.Color.FromRgb(23, 92, 211), (_, _) =>
            {
                _decision = ApprovalDecision.Approved;
                Close();
            });

            System.Windows.Controls.Button denyButton = CreateButton(options.DenyButtonText, System.Windows.Media.Color.FromRgb(185, 28, 28), (_, _) =>
            {
                _decision = ApprovalDecision.Denied;
                Close();
            });

            System.Windows.Controls.Button cancelButton = CreateButton(options.CancelButtonText, System.Windows.Media.Color.FromRgb(100, 116, 139), (_, _) =>
            {
                _decision = ApprovalDecision.Cancelled;
                Close();
            });

            Content = new Border
            {
                Padding = new Thickness(24),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = options.Headline,
                            FontSize = 20,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Margin = new Thickness(0, 14, 0, 0),
                            Text = options.Message,
                            TextWrapping = TextWrapping.Wrap
                        },
                        _countdownText,
                        new StackPanel
                        {
                            Margin = new Thickness(0, 24, 0, 0),
                            Orientation = System.Windows.Controls.Orientation.Horizontal,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                            Children =
                            {
                                cancelButton,
                                denyButton,
                                approveButton
                            }
                        }
                    }
                }
            };

            _timeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timeoutTimer.Tick += (_, _) => UpdateCountdown();
            Closed += (_, _) => _timeoutTimer.Stop();
            UpdateCountdown();
            _timeoutTimer.Start();
        }

        public static Task<ApprovalDecision> ShowAsync(ApprovalPromptOptions options, CancellationToken cancellationToken)
        {
            TaskCompletionSource<ApprovalDecision> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ApprovalPromptWindow window = new(options);

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (window.IsVisible)
                        {
                            window._decision = ApprovalDecision.Cancelled;
                            window.Close();
                        }
                    });
                });

                window.Closed += (_, _) => tcs.TrySetResult(window._decision);
                window.ShowDialog();
            });

            return tcs.Task;
        }

        private void UpdateCountdown()
        {
            TimeSpan remaining = _expiresAtUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _decision = ApprovalDecision.TimedOut;
                Close();
                return;
            }

            _countdownText.Text = $"응답 제한 시간: {remaining.Seconds + (remaining.Minutes * 60)}초 남음";
        }

        private static System.Windows.Controls.Button CreateButton(string text, System.Windows.Media.Color backgroundColor, RoutedEventHandler clickHandler)
        {
            System.Windows.Controls.Button button = new()
            {
                Content = text,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 8, 14, 8),
                Foreground = System.Windows.Media.Brushes.White,
                Background = new SolidColorBrush(backgroundColor),
                BorderBrush = System.Windows.Media.Brushes.Transparent
            };
            button.Click += clickHandler;
            return button;
        }
    }
}

internal static class ApprovalPolicyExtensions
{
    public static string ToDisplayText(this ApprovalPolicy policy)
    {
        return policy switch
        {
            ApprovalPolicy.PreApprovedDevice => "Pre-approved device",
            ApprovalPolicy.SupportRequest => "Support request",
            _ => "User approval"
        };
    }

    public static bool RequiresInteractiveApproval(this ApprovalPolicy policy)
    {
        return policy is ApprovalPolicy.UserApproval or ApprovalPolicy.SupportRequest;
    }

    public static string GetLogCategory(this ApprovalPolicy policy)
    {
        return policy switch
        {
            ApprovalPolicy.SupportRequest => "Support Approval",
            ApprovalPolicy.PreApprovedDevice => "Trusted Approval",
            _ => "Approval"
        };
    }
}
