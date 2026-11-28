using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Monarch.Core.Abstractions;
using Monarch.Core.Remote;
using Monarch.Desktop.Authentication;

namespace Monarch.Desktop.Central;

public partial class MainWindow : Window
{
    private readonly MsalAccessTokenProvider? _tokens;
    private readonly HttpClient? _tenantHttp;
    private readonly HttpClient? _ledgerHttp;
    private readonly ITenantDiscoveryClient? _tenantDiscovery;
    private readonly IGeneralLedgerStore? _ledger;
    private bool _changingSelection;
    private bool _busy;
    private bool _signedIn;

    public MainWindow()
    {
        InitializeComponent();
        TrialBalanceDateBox.Text = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var configuration = CentralDesktopConfiguration.LoadFromEnvironment();
            _tokens = new MsalAccessTokenProvider(configuration.Authentication);
            _tenantHttp = CreateHttpClient(new Uri(configuration.ApiBaseAddress, "api/v1/tenant-context/"));
            _ledgerHttp = CreateHttpClient(new Uri(configuration.ApiBaseAddress, "api/v1/general-ledger/"));
            _tenantDiscovery = new HttpTenantDiscoveryClient(_tenantHttp, _tokens);
            _ledger = new HttpGeneralLedgerStore(_ledgerHttp, _tokens);
            StatusText.Text = "Configuration validated. Sign in to discover authorized companies and branches.";
        }
        catch (Exception)
        {
            SignInButton.IsEnabled = false;
            StatusText.Text = "Central client configuration is invalid or incomplete. Verify the required environment variables.";
        }
    }

    private static HttpClient CreateHttpClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _ = await _tokens!.GetAccessTokenAsync();
            var companies = await _tenantDiscovery!.ListCompaniesAsync();
            _changingSelection = true;
            try
            {
                CompanyBox.ItemsSource = companies;
                CompanyBox.DisplayMemberPath = nameof(TenantCompany.LegalName);
                CompanyBox.SelectedValuePath = nameof(TenantCompany.CompanyId);
                CompanyBox.SelectedIndex = -1;
                BranchBox.ItemsSource = null;
                ClearLedgerViews();
            }
            finally
            {
                _changingSelection = false;
            }

            _signedIn = true;
            StatusText.Text = companies.Count == 0
                ? "Signed in. The identity has no active company grants."
                : $"Signed in. {companies.Count} authorized company record(s) discovered.";
        });
    }

    private async void Company_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || CompanyBox.SelectedItem is not TenantCompany company) return;
        await RunBusyAsync(async () =>
        {
            _changingSelection = true;
            try
            {
                BranchBox.ItemsSource = null;
                ClearLedgerViews();
            }
            finally
            {
                _changingSelection = false;
            }

            var branches = await _tenantDiscovery!.ListBranchesAsync(company.CompanyId);
            _changingSelection = true;
            try
            {
                BranchBox.ItemsSource = branches;
                BranchBox.DisplayMemberPath = nameof(TenantBranch.BranchName);
                BranchBox.SelectedValuePath = nameof(TenantBranch.BranchId);
                BranchBox.SelectedIndex = -1;
            }
            finally
            {
                _changingSelection = false;
            }
            StatusText.Text = $"Company {company.CompanyCode}: {branches.Count} authorized branch record(s) discovered.";
        });
    }

    private async void Branch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || CompanyBox.SelectedItem is not TenantCompany company ||
            BranchBox.SelectedItem is not TenantBranch branch) return;
        await RunBusyAsync(async () =>
        {
            ClearLedgerViews();
            var tenant = CreateTenantScope(company, branch);
            var accounts = await _ledger!.GetAccountsAsync(tenant, includeInactive: false);
            AccountsGrid.ItemsSource = accounts;
            StatusText.Text = $"Central API read succeeded. {accounts.Count} account record(s) loaded for {company.CompanyCode}/{branch.BranchCode}.";
        });
    }

    private async void RefreshJournals_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedTenant(out var tenant)) return;
        await RunBusyAsync(async () =>
        {
            var journals = await _ledger!.GetJournalsAsync(tenant, limit: 250);
            JournalsGrid.ItemsSource = journals;
            StatusText.Text = $"Loaded {journals.Count} journal record(s). Read-only view; no journal mutation is available here.";
        });
    }

    private async void RunTrialBalance_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedTenant(out var tenant)) return;
        if (!DateOnly.TryParseExact(TrialBalanceDateBox.Text.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOfDate))
        {
            StatusText.Text = "Enter the trial balance date as YYYY-MM-DD.";
            return;
        }
        var currency = CurrencyCodeBox.Text.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character < 'A' || character > 'Z'))
        {
            StatusText.Text = "Enter a three-letter ISO currency code.";
            return;
        }
        await RunBusyAsync(async () =>
        {
            var rows = await _ledger!.GetTrialBalanceAsync(tenant, asOfDate, currency);
            TrialBalanceGrid.ItemsSource = rows;
            StatusText.Text = $"Trial balance returned {rows.Count} row(s) as of {asOfDate:yyyy-MM-dd} in {currency}.";
        });
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await _tokens!.SignOutAsync();
            _signedIn = false;
            _changingSelection = true;
            try
            {
                CompanyBox.ItemsSource = null;
                BranchBox.ItemsSource = null;
                ClearLedgerViews();
            }
            finally
            {
                _changingSelection = false;
            }
            StatusText.Text = "Signed out. No local database fallback is available in this client.";
        });
    }

    private bool TryGetSelectedTenant(out TenantScope tenant)
    {
        if (_signedIn && CompanyBox.SelectedItem is TenantCompany company &&
            BranchBox.SelectedItem is TenantBranch branch)
        {
            tenant = CreateTenantScope(company, branch);
            return true;
        }
        tenant = null!;
        StatusText.Text = "Sign in and select an authorized company and branch first.";
        return false;
    }

    // The API derives the authenticated user from the validated bearer token.
    // Only the selected company and branch are sent as tenant selectors.
    private static TenantScope CreateTenantScope(TenantCompany company, TenantBranch branch) =>
        new(company.CompanyId, branch.BranchId, Guid.Empty);

    private void ClearLedgerViews()
    {
        AccountsGrid.ItemsSource = null;
        JournalsGrid.ItemsSource = null;
        TrialBalanceGrid.ItemsSource = null;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetInteractionEnabled(false);
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ClearLedgerViews();
            StatusText.Text = SafeFailureMessage(exception);
        }
        finally
        {
            _busy = false;
            SetInteractionEnabled(true);
        }
    }

    private void SetInteractionEnabled(bool enabled)
    {
        SignInButton.IsEnabled = enabled && !_signedIn && _tokens is not null;
        SignOutButton.IsEnabled = enabled && _signedIn;
        CompanyBox.IsEnabled = enabled && _signedIn && CompanyBox.Items.Count > 0;
        BranchBox.IsEnabled = enabled && _signedIn && BranchBox.Items.Count > 0;
        LedgerTabs.IsEnabled = enabled && _signedIn && BranchBox.SelectedItem is TenantBranch;
    }

    private static string SafeFailureMessage(Exception exception) => exception switch
    {
        LedgerAuthenticationException => "Authentication expired or was rejected. Sign in again; no local fallback was attempted.",
        UnauthorizedAccessException => "The server denied access to this company, branch, or operation. Verify the user's grants.",
        OperationCanceledException => "The central request was cancelled or timed out. Refresh the view before retrying.",
        HttpRequestException => "The central API could not complete the request. Check network connectivity and API availability.",
        ArgumentException => "The central API rejected the supplied request. Verify the selected company, branch, date, and currency.",
        _ => "The central operation failed. No local fallback was attempted. Check the application diagnostics."
    };

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_tokens is not null) await _tokens.DisposeAsync();
        _tenantHttp?.Dispose();
        _ledgerHttp?.Dispose();
    }
}
