#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.BIP78.Sender;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.ModelBinders;
using BTCPayServer.Models;
using BTCPayServer.Models.WalletViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Labels;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Services.Wallets.Export;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;
using NBXplorer.Client;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Controllers
{
    [Route("wallets")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [AutoValidateAntiforgeryToken]
    public partial class UIWalletsController : Controller
    {
        private StoreRepository Repository { get; }
        private WalletRepository WalletRepository { get; }
        private BTCPayNetworkProvider NetworkProvider { get; }
        private ExplorerClientProvider ExplorerClientProvider { get; }
        public IServiceProvider ServiceProvider { get; }
        public RateFetcher RateFetcher { get; }

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly NBXplorerDashboard _dashboard;
        private readonly IAuthorizationService _authorizationService;
        private readonly IFeeProviderFactory _feeRateProvider;
        private readonly BTCPayWalletProvider _walletProvider;
        private readonly WalletReceiveService _walletReceiveService;
        private readonly SettingsRepository _settingsRepository;
        private readonly DelayedTransactionBroadcaster _broadcaster;
        private readonly PayjoinClient _payjoinClient;
        private readonly LinkGenerator _linkGenerator;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly UTXOLocker _utxoLocker;
        private readonly WalletHistogramService _walletHistogramService;

        readonly CurrencyNameTable _currencyTable;

        public UIWalletsController(StoreRepository repo,
                                 WalletRepository walletRepository,
                                 CurrencyNameTable currencyTable,
                                 BTCPayNetworkProvider networkProvider,
                                 UserManager<ApplicationUser> userManager,
                                 NBXplorerDashboard dashboard,
                                 WalletHistogramService walletHistogramService,
                                 RateFetcher rateProvider,
                                 IAuthorizationService authorizationService,
                                 ExplorerClientProvider explorerProvider,
                                 IFeeProviderFactory feeRateProvider,
                                 BTCPayWalletProvider walletProvider,
                                 WalletReceiveService walletReceiveService,
                                 SettingsRepository settingsRepository,
                                 DelayedTransactionBroadcaster broadcaster,
                                 PayjoinClient payjoinClient,
                                 IServiceProvider serviceProvider,
                                 PullPaymentHostedService pullPaymentHostedService,
                                 UTXOLocker utxoLocker,
                                 LinkGenerator linkGenerator)
        {
            _currencyTable = currencyTable;
            _linkGenerator = linkGenerator;
            Repository = repo;
            WalletRepository = walletRepository;
            RateFetcher = rateProvider;
            _authorizationService = authorizationService;
            NetworkProvider = networkProvider;
            _userManager = userManager;
            _dashboard = dashboard;
            ExplorerClientProvider = explorerProvider;
            _feeRateProvider = feeRateProvider;
            _walletProvider = walletProvider;
            _walletReceiveService = walletReceiveService;
            _settingsRepository = settingsRepository;
            _broadcaster = broadcaster;
            _payjoinClient = payjoinClient;
            _pullPaymentHostedService = pullPaymentHostedService;
            _utxoLocker = utxoLocker;
            ServiceProvider = serviceProvider;
            _walletHistogramService = walletHistogramService;
        }

        [HttpPost]
        [Route("{walletId}")]
        public async Task<IActionResult> ModifyTransaction(
            // We need addlabel and addlabelclick. addlabel is the + button if the label does not exists,
            // addlabelclick is if the user click on existing label. For some reason, reusing the same name attribute for both
            // does not work
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, string transactionId,
            string? addlabel = null,
            string? addlabelclick = null,
            string? addcomment = null,
            string? removelabel = null)
        {
            addlabel = addlabel ?? addlabelclick;
            // Hack necessary when the user enter a empty comment and submit.
            // For some reason asp.net consider addcomment null instead of empty string...
            try
            {
                if (addcomment == null && Request?.Form?.TryGetValue(nameof(addcomment), out _) is true)
                {
                    addcomment = string.Empty;
                }
            }
            catch { }
            /////////

            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var txObjId = new WalletObjectId(walletId, WalletObjectData.Types.Tx, transactionId);
            var wallet = _walletProvider.GetWallet(paymentMethod.Network);
            if (addlabel != null)
            {
                await WalletRepository.AddWalletObjectLabels(txObjId, addlabel);
            }
            else if (removelabel != null)
            {
                await WalletRepository.RemoveWalletObjectLabels(txObjId, removelabel);
            }
            else if (addcomment != null)
            {
                await WalletRepository.SetWalletObjectComment(txObjId, addcomment);
            }
            return RedirectToAction(nameof(WalletTransactions), new { walletId = walletId.ToString() });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ListWallets()
        {
            if (GetUserId() == null)
            {
                return Challenge(AuthenticationSchemes.Cookie);
            }
            var wallets = new ListWalletsViewModel();
            var stores = await Repository.GetStoresByUserId(GetUserId());

            var onChainWallets = stores
                .SelectMany(s => s.GetSupportedPaymentMethods(NetworkProvider)
                    .OfType<DerivationSchemeSettings>()
                    .Select(d => ((Wallet: _walletProvider.GetWallet(d.Network),
                        DerivationStrategy: d.AccountDerivation,
                        Network: d.Network)))
                    .Where(_ => _.Wallet != null && _.Network.WalletSupported)
                    .Select(_ => (Wallet: _.Wallet,
                        Store: s,
                        Balance: GetBalanceString(_.Wallet, _.DerivationStrategy),
                        DerivationStrategy: _.DerivationStrategy,
                        Network: _.Network)))
                .ToList();

            foreach (var wallet in onChainWallets)
            {
                ListWalletsViewModel.WalletViewModel walletVm = new ListWalletsViewModel.WalletViewModel();
                wallets.Wallets.Add(walletVm);
                walletVm.Balance = await wallet.Balance + " " + wallet.Wallet.Network.CryptoCode;
                walletVm.IsOwner = wallet.Store.Role == StoreRoles.Owner;
                if (!walletVm.IsOwner)
                {
                    walletVm.Balance = "";
                }

                walletVm.CryptoCode = wallet.Network.CryptoCode;
                walletVm.StoreId = wallet.Store.Id;
                walletVm.Id = new WalletId(wallet.Store.Id, wallet.Network.CryptoCode);
                walletVm.StoreName = wallet.Store.StoreName;

                var money = await GetBalanceAsMoney(wallet.Wallet, wallet.DerivationStrategy);
                wallets.BalanceForCryptoCode[wallet.Network] = wallets.BalanceForCryptoCode.ContainsKey(wallet.Network)
                    ? wallets.BalanceForCryptoCode[wallet.Network].Add(money)
                    : money;
            }

            return View(wallets);
        }

        [HttpGet("{walletId}")]
        [HttpGet("{walletId}/transactions")]
        public async Task<IActionResult> WalletTransactions(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId,
            string? labelFilter = null,
            int skip = 0,
            int count = 50
        )
        {
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var wallet = _walletProvider.GetWallet(paymentMethod.Network);

            // We can't filter at the database level if we need to apply label filter
            var preFiltering = string.IsNullOrEmpty(labelFilter);
            var transactions = await wallet.FetchTransactionHistory(paymentMethod.AccountDerivation, preFiltering ? skip : null, preFiltering ? count : null);
            var walletTransactionsInfo = await WalletRepository.GetWalletTransactionsInfo(walletId, transactions.Select(t => t.TransactionId.ToString()).ToArray());
            var model = new ListTransactionsViewModel { Skip = skip, Count = count };
            model.Labels.AddRange(
                (await WalletRepository.GetWalletLabels(walletId))
                .Select(c => (c.Label, c.Color, ColorPalette.Default.TextColor(c.Color)))
                );

            if (labelFilter != null)
            {
                model.PaginationQuery = new Dictionary<string, object> { { "labelFilter", labelFilter } };
            }
            if (transactions == null)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = StatusMessageModel.StatusSeverity.Error,
                    Message =
                        "There was an error retrieving the transactions list. Is NBXplorer configured correctly?"
                });
                model.Transactions = new List<ListTransactionsViewModel.TransactionViewModel>();
            }
            else
            {
                foreach (var tx in transactions)
                {
                    var vm = new ListTransactionsViewModel.TransactionViewModel();
                    vm.Id = tx.TransactionId.ToString();
                    vm.Link = string.Format(CultureInfo.InvariantCulture, paymentMethod.Network.BlockExplorerLink,
                        vm.Id);
                    vm.Timestamp = tx.SeenAt;
                    vm.Positive = tx.BalanceChange.GetValue(wallet.Network) >= 0;
                    vm.Balance = tx.BalanceChange.ShowMoney(wallet.Network);
                    vm.IsConfirmed = tx.Confirmations != 0;

                    if (walletTransactionsInfo.TryGetValue(tx.TransactionId.ToString(), out var transactionInfo))
                    {
                        var labels = CreateTransactionTagModels(transactionInfo);
                        vm.Tags.AddRange(labels);
                        vm.Comment = transactionInfo.Comment;
                    }

                    if (labelFilter == null ||
                        vm.Tags.Any(l => l.Text.Equals(labelFilter, StringComparison.OrdinalIgnoreCase)))
                        model.Transactions.Add(vm);
                }

                model.Total = preFiltering ? null : model.Transactions.Count;
                // if we couldn't filter at the db level, we need to apply skip and count
                if (!preFiltering)
                {
                    model.Transactions = model.Transactions.Skip(skip).Take(count).ToList();
                }
            }

            model.CryptoCode = walletId.CryptoCode;

            //If ajax call then load the partial view
            return Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                ? PartialView("_WalletTransactionsList", model)
                : View(model);
        }

        [HttpGet("{walletId}/histogram/{type}")]
        public async Task<IActionResult> WalletHistogram(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, WalletHistogramType type)
        {
            var store = GetCurrentStore();
            var data = await _walletHistogramService.GetHistogram(store, walletId, type);

            return data == null
                ? NotFound()
                : Json(data);
        }

        [HttpGet("{walletId}/receive")]
        public IActionResult WalletReceive([ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            [FromQuery] string? returnUrl = null)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null)
                return NotFound();
            var store = GetCurrentStore();
            var address = _walletReceiveService.Get(walletId)?.Address;
            var allowedPayjoin = paymentMethod.IsHotWallet && store.GetStoreBlob().PayJoinEnabled;
            var bip21 = network.GenerateBIP21(address?.ToString(), null);
            if (allowedPayjoin)
            {
                bip21.QueryParams.Add(PayjoinClient.BIP21EndpointKey,
                    Request.GetAbsoluteUri(Url.Action(nameof(PayJoinEndpointController.Submit), "PayJoinEndpoint",
                        new { walletId.CryptoCode })));
            }
            return View(new WalletReceiveViewModel
            {
                CryptoCode = walletId.CryptoCode,
                Address = address?.ToString(),
                CryptoImage = GetImage(paymentMethod.PaymentId, network),
                PaymentLink = bip21.ToString(),
                ReturnUrl = returnUrl ?? HttpContext.Request.GetTypedHeaders().Referer?.AbsolutePath
            });
        }

        [HttpPost("{walletId}/receive")]
        public async Task<IActionResult> WalletReceive([ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            WalletReceiveViewModel vm, string command)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null)
                return NotFound();
            switch (command)
            {
                case "unreserve-current-address":
                    var address = await _walletReceiveService.UnReserveAddress(walletId);
                    if (!string.IsNullOrEmpty(address))
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            AllowDismiss = true,
                            Message = $"Address {address} was unreserved.",
                            Severity = StatusMessageModel.StatusSeverity.Success,
                        });
                    }
                    break;
                case "generate-new-address":
                    await _walletReceiveService.GetOrGenerate(walletId, true);
                    break;
                case "fill-wallet":
                    var cheater = ServiceProvider.GetService<Cheater>();
                    if (cheater != null)
                        await SendFreeMoney(cheater, walletId, paymentMethod);
                    break;
            }
            return RedirectToAction(nameof(WalletReceive), new { walletId, returnUrl = vm.ReturnUrl });
        }

        private async Task SendFreeMoney(Cheater cheater, WalletId walletId, DerivationSchemeSettings paymentMethod)
        {
            var c = this.ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode);
            var addresses = Enumerable.Range(0, 200).Select(_ => c.GetUnusedAsync(paymentMethod.AccountDerivation, DerivationFeature.Deposit, reserve: true)).ToArray();
            await Task.WhenAll(addresses);
            await cheater.CashCow.GenerateAsync(addresses.Length / 8);
            var b = cheater.CashCow.PrepareBatch();
            Random r = new Random();
            List<Task<uint256>> sending = new List<Task<uint256>>();
            foreach (var a in addresses)
            {
                sending.Add(b.SendToAddressAsync((await a).Address, Money.Coins(0.1m) + Money.Satoshis(r.Next(0, 90_000_000))));
            }
            await b.SendBatchAsync();
            await cheater.CashCow.GenerateAsync(1);

            var factory = ServiceProvider.GetRequiredService<NBXplorerConnectionFactory>();

            // Wait it sync...
            await Task.Delay(1000);
            await ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode).WaitServerStartedAsync();
            await Task.Delay(1000);
            await using var conn = await factory.OpenConnection();
            var wallet_id = paymentMethod.GetNBXWalletId();

            var txIds = sending.Select(s => s.Result.ToString()).ToArray();
            await conn.ExecuteAsync(
                "UPDATE txs t SET seen_at=(NOW() - (random() * (interval '90 days'))) " +
                "FROM unnest(@txIds) AS r (tx_id) WHERE r.tx_id=t.tx_id;", new { txIds });
            await Task.Delay(1000);
            await conn.ExecuteAsync("REFRESH MATERIALIZED VIEW wallets_history;");
        }

        private async Task<bool> CanUseHotWallet()
        {
            var policies = await _settingsRepository.GetSettingAsync<PoliciesSettings>();
            return (await _authorizationService.CanUseHotWallet(policies, User)).HotWallet;
        }

        [HttpGet("{walletId}/send")]
        public async Task<IActionResult> WalletSend(
            [ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            string? defaultDestination = null, string? defaultAmount = null, string[]? bip21 = null,
            [FromQuery] string? returnUrl = null)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var store = await Repository.FindStore(walletId.StoreId, GetUserId());
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null || store is null)
                return NotFound();
            var network = this.NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null || network.ReadonlyWallet)
                return NotFound();
            var storeData = store.GetStoreBlob();
            var rateRules = store.GetStoreBlob().GetRateRules(NetworkProvider);
            rateRules.Spread = 0.0m;
            var currencyPair = new Rating.CurrencyPair(paymentMethod.PaymentId.CryptoCode, storeData.DefaultCurrency);
            double.TryParse(defaultAmount, out var amount);

            var model = new WalletSendModel
            {
                CryptoCode = walletId.CryptoCode,
                ReturnUrl = returnUrl ?? HttpContext.Request.GetTypedHeaders().Referer?.AbsolutePath
            };
            if (bip21?.Any() is true)
            {
                foreach (var link in bip21)
                {
                    if (!string.IsNullOrEmpty(link))
                    {
                        LoadFromBIP21(model, link, network);
                    }
                }
            }

            if (!(model.Outputs?.Any() is true))
            {
                model.Outputs = new List<WalletSendModel.TransactionOutput>()
                {
                    new WalletSendModel.TransactionOutput()
                    {
                        Amount = Convert.ToDecimal(amount), DestinationAddress = defaultDestination
                    }
                };
            }
            var feeProvider = _feeRateProvider.CreateFeeProvider(network);
            var recommendedFees =
                new[]
                    {
                        TimeSpan.FromMinutes(10.0), TimeSpan.FromMinutes(60.0), TimeSpan.FromHours(6.0),
                        TimeSpan.FromHours(24.0),
                    }.Select(async time =>
                    {
                        try
                        {
                            var result = await feeProvider.GetFeeRateAsync(
                                (int)network.NBitcoinNetwork.Consensus.GetExpectedBlocksFor(time));
                            return new WalletSendModel.FeeRateOption()
                            {
                                Target = time,
                                FeeRate = result.SatoshiPerByte
                            };
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    })
                    .ToArray();
            var balance = _walletProvider.GetWallet(network).GetBalance(paymentMethod.AccountDerivation);
            model.NBXSeedAvailable = await GetSeed(walletId, network) != null;
            var Balance = await balance;
            model.CurrentBalance = (Balance.Available ?? Balance.Total).GetValue(network);
            if (Balance.Immature is null)
                model.ImmatureBalance = 0;
            else
                model.ImmatureBalance = Balance.Immature.GetValue(network);

            await Task.WhenAll(recommendedFees);
            model.RecommendedSatoshiPerByte =
                recommendedFees.Select(tuple => tuple.Result).Where(option => option != null).ToList();

            model.FeeSatoshiPerByte = model.RecommendedSatoshiPerByte.LastOrDefault()?.FeeRate;
            model.SupportRBF = network.SupportRBF;

            model.CryptoDivisibility = network.Divisibility;
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                try
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await RateFetcher.FetchRate(currencyPair, rateRules, cts.Token)
                        .WithCancellation(cts.Token);
                    if (result.BidAsk != null)
                    {
                        model.Rate = result.BidAsk.Center;
                        model.FiatDivisibility = _currencyTable.GetNumberFormatInfo(currencyPair.Right, true)
                            .CurrencyDecimalDigits;
                        model.Fiat = currencyPair.Right;
                    }
                    else
                    {
                        model.RateError =
                            $"{result.EvaluatedRule} ({string.Join(", ", result.Errors.OfType<object>().ToArray())})";
                    }
                }
                catch (Exception ex) { model.RateError = ex.Message; }
            }

            return View(model);
        }

        private async Task<string?> GetSeed(WalletId walletId, BTCPayNetwork network)
        {
            return await CanUseHotWallet() &&
                   GetDerivationSchemeSettings(walletId) is DerivationSchemeSettings s &&
                   s.IsHotWallet &&
                   ExplorerClientProvider.GetExplorerClient(network) is ExplorerClient client &&
                   await client.GetMetadataAsync<string>(s.AccountDerivation, WellknownMetadataKeys.MasterHDKey) is
                       string seed &&
                   !string.IsNullOrEmpty(seed)
                ? seed
                : null;
        }

        [HttpPost("{walletId}/send")]
        public async Task<IActionResult> WalletSend(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, WalletSendModel vm, string command = "", CancellationToken cancellation = default,
            string? bip21 = "")
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var store = await Repository.FindStore(walletId.StoreId, GetUserId());
            if (store == null)
                return NotFound();
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null || network.ReadonlyWallet)
                return NotFound();

            vm.SupportRBF = network.SupportRBF;
            vm.NBXSeedAvailable = await GetSeed(walletId, network) != null;
            if (!string.IsNullOrEmpty(bip21))
            {
                vm.Outputs?.Clear();
                LoadFromBIP21(vm, bip21, network);
            }

            decimal transactionAmountSum = 0;
            if (command == "toggle-input-selection")
            {
                vm.InputSelection = !vm.InputSelection;
            }
            if (vm.InputSelection)
            {
                var schemeSettings = GetDerivationSchemeSettings(walletId);
                if (schemeSettings is null)
                    return NotFound();

                var utxos = await _walletProvider.GetWallet(network)
                    .GetUnspentCoins(schemeSettings.AccountDerivation, false, cancellation);

                var walletTransactionsInfoAsync = await this.WalletRepository.GetWalletTransactionsInfo(walletId,
                    utxos.SelectMany(u => GetWalletObjectsQuery.Get(u)).Distinct().ToArray());
                vm.InputsAvailable = utxos.Select(coin =>
                {
                    walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.Hash.ToString(), out var info1);
                    walletTransactionsInfoAsync.TryGetValue(coin.Address.ToString(), out var info2);
                    walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.ToString(), out var info3);
                    var info = WalletRepository.Merge(info1, info2, info3);
                    return new WalletSendModel.InputSelectionOption()
                    {
                        Outpoint = coin.OutPoint.ToString(),
                        Amount = coin.Value.GetValue(network),
                        Comment = info?.Comment,
                        Labels = CreateTransactionTagModels(info),
                        Link = string.Format(CultureInfo.InvariantCulture, network.BlockExplorerLink,
                            coin.OutPoint.Hash.ToString()),
                        Confirmations = coin.Confirmations
                    };
                }).ToArray();
            }

            if (command == "toggle-input-selection")
            {
                ModelState.Clear();
                return View(vm);
            }
            vm.Outputs ??= new();
            if (!string.IsNullOrEmpty(bip21))
            {
                if (!vm.Outputs.Any())
                {
                    vm.Outputs.Add(new WalletSendModel.TransactionOutput());
                }
                return View(vm);
            }
            if (command == "add-output")
            {
                ModelState.Clear();
                vm.Outputs.Add(new WalletSendModel.TransactionOutput());
                return View(vm);
            }
            if (command.StartsWith("remove-output", StringComparison.InvariantCultureIgnoreCase))
            {
                ModelState.Clear();
                var index = int.Parse(
                    command.Substring(command.IndexOf(":", StringComparison.InvariantCultureIgnoreCase) + 1),
                    CultureInfo.InvariantCulture);
                vm.Outputs.RemoveAt(index);
                return View(vm);
            }

            if (!vm.Outputs.Any())
            {
                ModelState.AddModelError(string.Empty,
                    "Please add at least one transaction output");
                return View(vm);
            }

            var bypassBalanceChecks = command == "schedule";

            var subtractFeesOutputsCount = new List<int>();
            var substractFees = vm.Outputs.Any(o => o.SubtractFeesFromOutput);
            for (var i = 0; i < vm.Outputs.Count; i++)
            {
                var transactionOutput = vm.Outputs[i];
                if (transactionOutput.SubtractFeesFromOutput)
                {
                    subtractFeesOutputsCount.Add(i);
                }
                transactionOutput.DestinationAddress = transactionOutput.DestinationAddress?.Trim() ?? string.Empty;

                var inputName =
                    string.Format(CultureInfo.InvariantCulture, "Outputs[{0}].",
                        i.ToString(CultureInfo.InvariantCulture)) +
                    nameof(transactionOutput.DestinationAddress);
                try
                {
                    var address = BitcoinAddress.Create(transactionOutput.DestinationAddress, network.NBitcoinNetwork);
                    if (address is TaprootAddress)
                    {
                        var supportTaproot = _dashboard.Get(network.CryptoCode)?.Status?.BitcoinStatus?.Capabilities
                            ?.CanSupportTaproot;
                        if (!(supportTaproot is true))
                        {
                            ModelState.AddModelError(inputName,
                                "You need to update your full node, and/or NBXplorer (Version >= 2.1.56) to be able to send to a taproot address.");
                        }
                    }
                }
                catch
                {
                    ModelState.AddModelError(inputName, "Invalid address");
                }

                if (!bypassBalanceChecks && transactionOutput.Amount.HasValue)
                {
                    transactionAmountSum += transactionOutput.Amount.Value;

                    if (vm.CurrentBalance == transactionOutput.Amount.Value &&
                        !transactionOutput.SubtractFeesFromOutput)
                        vm.AddModelError(model => model.Outputs[i].SubtractFeesFromOutput,
                            "You are sending your entire balance to the same destination, you should subtract the fees",
                            this);
                }
            }

            if (!bypassBalanceChecks)
            {
                if (subtractFeesOutputsCount.Count > 1)
                {
                    foreach (var subtractFeesOutput in subtractFeesOutputsCount)
                    {
                        vm.AddModelError(model => model.Outputs[subtractFeesOutput].SubtractFeesFromOutput,
                            "You can only subtract fees from one output", this);
                    }
                }
                else if (vm.CurrentBalance == transactionAmountSum && !substractFees)
                {
                    ModelState.AddModelError(string.Empty,
                        "You are sending your entire balance, you should subtract the fees from an output");
                }

                if (vm.CurrentBalance < transactionAmountSum)
                {
                    for (var i = 0; i < vm.Outputs.Count; i++)
                    {
                        vm.AddModelError(model => model.Outputs[i].Amount,
                            "You are sending more than what you own", this);
                    }
                }

                if (vm.FeeSatoshiPerByte is decimal fee)
                {
                    if (fee < 0)
                    {
                        vm.AddModelError(model => model.FeeSatoshiPerByte,
                            "The fee rate should be above 0", this);
                    }
                }
            }

            if (!ModelState.IsValid)
                return View(vm);

            var derivationScheme = GetDerivationSchemeSettings(walletId);
            if (derivationScheme is null)
                return NotFound();
            CreatePSBTResponse psbtResponse;
            if (command == "schedule")
            {
                var pmi = new PaymentMethodId(walletId.CryptoCode, BitcoinPaymentType.Instance);
                var claims =
                    vm.Outputs.Where(output => string.IsNullOrEmpty(output.PayoutId)).Select(output => new ClaimRequest()
                    {
                        Destination = new AddressClaimDestination(
                            BitcoinAddress.Create(output.DestinationAddress, network.NBitcoinNetwork)),
                        Value = output.Amount,
                        PaymentMethodId = pmi,
                        StoreId = walletId.StoreId,
                        PreApprove = true,
                    }).ToArray();
                var someFailed = false;
                string? message = null;
                string? errorMessage = null;
                var result = new Dictionary<ClaimRequest, ClaimRequest.ClaimResult>();
                foreach (ClaimRequest claimRequest in claims)
                {
                    var response = await _pullPaymentHostedService.Claim(claimRequest);
                    result.Add(claimRequest, response.Result);
                    if (response.Result == ClaimRequest.ClaimResult.Ok)
                    {
                        if (message is null)
                        {
                            message = "Payouts scheduled:<br/>";
                        }

                        message += $"{claimRequest.Value} to {claimRequest.Destination.ToString()}<br/>";

                    }
                    else
                    {
                        someFailed = true;
                        if (errorMessage is null)
                        {
                            errorMessage = "Payouts failed to be scheduled:<br/>";
                        }

                        switch (response.Result)
                        {
                            case ClaimRequest.ClaimResult.Duplicate:
                                errorMessage += $"{claimRequest.Value} to {claimRequest.Destination.ToString() } - address reuse<br/>";
                                break;
                            case ClaimRequest.ClaimResult.AmountTooLow:
                                errorMessage += $"{claimRequest.Value} to {claimRequest.Destination.ToString() } - amount too low<br/>";
                                break;
                        }
                    }
                }

                if (message is not null && errorMessage is not null)
                {
                    message += $"<br/><br/>{errorMessage}";
                }
                else if (message is null && errorMessage is not null)
                {
                    message = errorMessage;
                }
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = someFailed ? StatusMessageModel.StatusSeverity.Warning :
                        StatusMessageModel.StatusSeverity.Success,
                    Html = message
                });
                return RedirectToAction("Payouts", "UIStorePullPayments",
                    new
                    {
                        storeId = walletId.StoreId,
                        PaymentMethodId = pmi.ToString(),
                        payoutState = PayoutState.AwaitingPayment,
                    });
            }

            try
            {
                psbtResponse = await CreatePSBT(network, derivationScheme, vm, cancellation);
            }
            catch (NBXplorerException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Error.Message);
                return View(vm);
            }
            catch (NotSupportedException)
            {
                ModelState.AddModelError(string.Empty, "You need to update your version of NBXplorer");
                return View(vm);
            }

            var psbt = psbtResponse.PSBT;
            derivationScheme.RebaseKeyPaths(psbt);

            var signingContext = new SigningContextModel
            {
                PayJoinBIP21 = vm.PayJoinBIP21,
                EnforceLowR = psbtResponse.Suggestions?.ShouldEnforceLowR,
                ChangeAddress = psbtResponse.ChangeAddress?.ToString(),
                PSBT = psbt.ToHex()
            };
            switch (command)
            {
                case "sign":
                    return await WalletSign(walletId, new WalletPSBTViewModel
                    {
                        SigningContext = signingContext,
                        ReturnUrl = vm.ReturnUrl,
                        BackUrl = vm.BackUrl
                    });
                case "analyze-psbt":
                    var name =
                        $"Send-{string.Join('_', vm.Outputs.Select(output => $"{output.Amount}->{output.DestinationAddress}{(output.SubtractFeesFromOutput ? "-Fees" : string.Empty)}"))}.psbt";
                    return RedirectToWalletPSBT(new WalletPSBTViewModel { PSBT = psbt.ToBase64(), FileName = name });
                default:
                    return View(vm);
            }
        }


        private void LoadFromBIP21(WalletSendModel vm, string bip21, BTCPayNetwork network)
        {
            vm.Outputs ??= new();
            try
            {
                var uriBuilder = new NBitcoin.Payment.BitcoinUrlBuilder(bip21, network.NBitcoinNetwork);

                vm.Outputs.Add(new WalletSendModel.TransactionOutput()
                {
                    Amount = uriBuilder.Amount?.ToDecimal(MoneyUnit.BTC),
                    DestinationAddress = uriBuilder.Address?.ToString(),
                    SubtractFeesFromOutput = false,
                    PayoutId = uriBuilder.UnknownParameters.ContainsKey("payout")
                        ? uriBuilder.UnknownParameters["payout"]
                        : null
                });
                if (!string.IsNullOrEmpty(uriBuilder.Label) || !string.IsNullOrEmpty(uriBuilder.Message))
                {
                    TempData.SetStatusMessageModel(new StatusMessageModel()
                    {
                        Severity = StatusMessageModel.StatusSeverity.Info,
                        Html =
                            $"Payment {(string.IsNullOrEmpty(uriBuilder.Label) ? string.Empty : $" to {uriBuilder.Label}")} {(string.IsNullOrEmpty(uriBuilder.Message) ? string.Empty : $" for {uriBuilder.Message}")}"
                    });
                }

                if (uriBuilder.TryGetPayjoinEndpoint(out _))
                    vm.PayJoinBIP21 = uriBuilder.ToString();
            }
            catch
            {
                try
                {
                    vm.Outputs.Add(new WalletSendModel.TransactionOutput()
                    {
                        DestinationAddress = BitcoinAddress.Create(bip21, network.NBitcoinNetwork).ToString()
                    }
                    );
                }
                catch
                {
                    TempData.SetStatusMessageModel(new StatusMessageModel()
                    {
                        Severity = StatusMessageModel.StatusSeverity.Error,
                        Message = "The provided BIP21 payment URI was malformed"
                    });
                }
            }

            ModelState.Clear();
        }

        private IActionResult ViewVault(WalletId walletId, WalletPSBTViewModel vm)
        {
            return View(nameof(WalletSendVault),
                new WalletSendVaultModel
                {
                    SigningContext = vm.SigningContext,
                    WalletId = walletId.ToString(),
                    WebsocketPath = Url.Action(nameof(UIVaultController.VaultBridgeConnection), "UIVault",
                        new { walletId = walletId.ToString() }),
                    ReturnUrl = vm.ReturnUrl,
                    BackUrl = vm.BackUrl
                });
        }

        [HttpPost("{walletId}/vault")]
        public IActionResult WalletSendVault([ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            WalletSendVaultModel model)
        {
            return RedirectToWalletPSBTReady(new WalletPSBTReadyViewModel
            {
                SigningContext = model.SigningContext,
                ReturnUrl = model.ReturnUrl,
                BackUrl = model.BackUrl
            });
        }

        private IActionResult RedirectToWalletPSBTReady(WalletPSBTReadyViewModel vm)
        {
            var redirectVm = new PostRedirectViewModel
            {
                AspController = "UIWallets",
                AspAction = nameof(WalletPSBTReady),
                RouteParameters = { { "walletId", this.RouteData?.Values["walletId"]?.ToString() } },
                FormParameters =
                {
                    { "SigningKey", vm.SigningKey },
                    { "SigningKeyPath", vm.SigningKeyPath },
                    { "command", "decode" }
                }
            };
            AddSigningContext(redirectVm, vm.SigningContext);
            if (!string.IsNullOrEmpty(vm.SigningContext.OriginalPSBT) &&
                !string.IsNullOrEmpty(vm.SigningContext.PSBT))
            {
                //if a hw device signed a payjoin, we want it broadcast instantly
                redirectVm.FormParameters.Remove("command");
                redirectVm.FormParameters.Add("command", "broadcast");
            }
            if (vm.ReturnUrl != null)
            {
                redirectVm.FormParameters.Add("returnUrl", vm.ReturnUrl);
            }
            if (vm.BackUrl != null)
            {
                redirectVm.FormParameters.Add("backUrl", vm.BackUrl);
            }
            return View("PostRedirect", redirectVm);
        }

        private void AddSigningContext(PostRedirectViewModel redirectVm, SigningContextModel signingContext)
        {
            if (signingContext is null)
                return;
            redirectVm.FormParameters.Add("SigningContext.PSBT", signingContext.PSBT);
            redirectVm.FormParameters.Add("SigningContext.OriginalPSBT", signingContext.OriginalPSBT);
            redirectVm.FormParameters.Add("SigningContext.PayJoinBIP21", signingContext.PayJoinBIP21);
            redirectVm.FormParameters.Add("SigningContext.EnforceLowR",
                signingContext.EnforceLowR?.ToString(CultureInfo.InvariantCulture));
            redirectVm.FormParameters.Add("SigningContext.ChangeAddress", signingContext.ChangeAddress);
        }

        private IActionResult RedirectToWalletPSBT(WalletPSBTViewModel vm)
        {
            var redirectVm = new PostRedirectViewModel
            {
                AspController = "UIWallets",
                AspAction = nameof(WalletPSBT),
                RouteParameters = { { "walletId", RouteData.Values["walletId"]?.ToString() } },
                FormParameters =
                {
                    { "psbt", vm.PSBT },
                    { "fileName", vm.FileName },
                    { "backUrl", vm.BackUrl },
                    { "returnUrl", vm.ReturnUrl },
                    { "command", "decode" }
                }
            };
            return View("PostRedirect", redirectVm);
        }

        [HttpGet("{walletId}/psbt/seed")]
        public IActionResult SignWithSeed([ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            SigningContextModel signingContext, string returnUrl, string backUrl)
        {
            return View(nameof(SignWithSeed), new SignWithSeedViewModel
            {
                SigningContext = signingContext,
                ReturnUrl = returnUrl,
                BackUrl = backUrl
            });
        }

        [HttpPost("{walletId}/psbt/seed")]
        public async Task<IActionResult> SignWithSeed([ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            SignWithSeedViewModel viewModel)
        {
            if (!ModelState.IsValid)
            {
                return View("SignWithSeed", viewModel);
            }
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null)
                throw new FormatException("Invalid value for crypto code");
            ExtKey extKey = viewModel.GetExtKey(network.NBitcoinNetwork);

            if (extKey is null)
            {
                ModelState.AddModelError(nameof(viewModel.SeedOrKey),
                    "Seed or Key was not in a valid format. It is either the 12/24 words or starts with xprv");
            }

            var psbt = PSBT.Parse(viewModel.SigningContext.PSBT, network.NBitcoinNetwork);

            if (!psbt.IsReadyToSign())
            {
                ModelState.AddModelError(nameof(viewModel.SigningContext.PSBT), "PSBT is not ready to be signed");
            }

            if (!ModelState.IsValid)
            {
                return View("SignWithSeed", viewModel);
            }
            // It will never throw, this make nullable check below happy
            ArgumentNullException.ThrowIfNull(extKey);

            ExtKey? signingKey = null;
            var settings = GetDerivationSchemeSettings(walletId);
            if (settings is null)
                return NotFound();
            var signingKeySettings = settings.GetSigningAccountKeySettings();
            if (signingKeySettings.RootFingerprint is null)
                signingKeySettings.RootFingerprint = extKey.GetPublicKey().GetHDFingerPrint();

            RootedKeyPath rootedKeyPath = signingKeySettings.GetRootedKeyPath();
            if (rootedKeyPath == null)
            {
                ModelState.AddModelError(nameof(viewModel.SeedOrKey),
                    "The master fingerprint and/or account key path of your seed are not set in the wallet settings.");
                return View(nameof(SignWithSeed), viewModel);
            }
            // The user gave the root key, let's try to rebase the PSBT, and derive the account private key
            if (rootedKeyPath.MasterFingerprint == extKey.GetPublicKey().GetHDFingerPrint())
            {
                psbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
                signingKey = extKey.Derive(rootedKeyPath.KeyPath);
            }
            else
            {
                ModelState.AddModelError(nameof(viewModel.SeedOrKey),
                    "The master fingerprint does not match the one set in your wallet settings. Probable causes are: wrong seed, wrong passphrase or wrong fingerprint in your wallet settings.");
                return View(nameof(SignWithSeed), viewModel);
            }

            psbt.Settings.SigningOptions = new SigningOptions()
            {
                EnforceLowR = !(viewModel.SigningContext?.EnforceLowR is false)
            };
            var changed = psbt.PSBTChanged(() => psbt.SignAll(settings.AccountDerivation, signingKey, rootedKeyPath));
            if (!changed)
            {
                var update = new UpdatePSBTRequest() { PSBT = psbt, DerivationScheme = settings.AccountDerivation };
                update.RebaseKeyPaths = settings.GetPSBTRebaseKeyRules().ToList();
                psbt = (await ExplorerClientProvider.GetExplorerClient(network).UpdatePSBTAsync(update))?.PSBT;
                changed = psbt is not null && psbt.PSBTChanged(() =>
                    psbt.SignAll(settings.AccountDerivation, signingKey, rootedKeyPath));
                if (!changed)
                {
                    ModelState.AddModelError(nameof(viewModel.SeedOrKey),
                        "Impossible to sign the transaction. Probable causes: Incorrect account key path in wallet settings or PSBT already signed.");
                    return View(nameof(SignWithSeed), viewModel);
                }
            }
            ModelState.Remove(nameof(viewModel.SigningContext.PSBT));
            viewModel.SigningContext ??= new();
            viewModel.SigningContext.PSBT = psbt?.ToBase64();
            return RedirectToWalletPSBTReady(new WalletPSBTReadyViewModel
            {
                SigningKey = signingKey.GetWif(network.NBitcoinNetwork).ToString(),
                SigningKeyPath = rootedKeyPath?.ToString(),
                SigningContext = viewModel.SigningContext,
                ReturnUrl = viewModel.ReturnUrl,
                BackUrl = viewModel.BackUrl
            });
        }

        private string ValueToString(Money v, BTCPayNetworkBase network)
        {
            return v.ToString() + " " + network.CryptoCode;
        }

        [HttpGet("{walletId}/rescan")]
        public async Task<IActionResult> WalletRescan(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var vm = new RescanWalletModel();
            vm.IsFullySync = _dashboard.IsFullySynched(walletId.CryptoCode, out var unused);
            vm.IsServerAdmin = (await _authorizationService.AuthorizeAsync(User, Policies.CanModifyServerSettings))
                .Succeeded;
            vm.IsSupportedByCurrency =
                _dashboard.Get(walletId.CryptoCode)?.Status?.BitcoinStatus?.Capabilities?.CanScanTxoutSet == true;
            var explorer = ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode);
            var scanProgress = await explorer.GetScanUTXOSetInformationAsync(paymentMethod.AccountDerivation);
            if (scanProgress != null)
            {
                vm.PreviousError = scanProgress.Error;
                if (scanProgress.Status == ScanUTXOStatus.Queued || scanProgress.Status == ScanUTXOStatus.Pending)
                {
                    if (scanProgress.Progress == null)
                    {
                        vm.Progress = 0;
                    }
                    else
                    {
                        vm.Progress = scanProgress.Progress.OverallProgress;
                        vm.RemainingTime = TimeSpan.FromSeconds(scanProgress.Progress.RemainingSeconds).PrettyPrint();
                    }
                }

                if (scanProgress.Status == ScanUTXOStatus.Complete)
                {
                    vm.LastSuccess = scanProgress.Progress;
                    vm.TimeOfScan = (scanProgress.Progress!.CompletedAt!.Value - scanProgress.Progress.StartedAt)
                        .PrettyPrint();
                }
            }

            return View(vm);
        }

        [HttpPost("{walletId}/rescan")]
        [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> WalletRescan(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, RescanWalletModel vm)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();
            var explorer = ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode);
            try
            {
                await explorer.ScanUTXOSetAsync(paymentMethod.AccountDerivation, vm.BatchSize, vm.GapLimit,
                    vm.StartingIndex);
                _walletProvider.GetWallet(walletId.CryptoCode).InvalidateCache(paymentMethod.AccountDerivation);
            }
            catch (NBXplorerException ex) when (ex.Error.Code == "scanutxoset-in-progress")
            {
            }

            return RedirectToAction();
        }

        internal DerivationSchemeSettings? GetDerivationSchemeSettings(WalletId walletId)
        {
            return GetCurrentStore().GetDerivationSchemeSettings(NetworkProvider, walletId.CryptoCode);
        }

        private static async Task<IMoney> GetBalanceAsMoney(BTCPayWallet wallet,
            DerivationStrategyBase derivationStrategy)
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var b = await wallet.GetBalance(derivationStrategy, cts.Token);
                return b.Available ?? b.Total;
            }
            catch
            {
                return Money.Zero;
            }
        }

        internal async Task<string> GetBalanceString(BTCPayWallet wallet, DerivationStrategyBase? derivationStrategy)
        {
            if (derivationStrategy is null)
                return "--";
            try
            {
                return (await GetBalanceAsMoney(wallet, derivationStrategy)).ShowMoney(wallet.Network);
            }
            catch
            {
                return "--";
            }
        }

        [HttpPost("{walletId}/actions")]
        public async Task<IActionResult> WalletActions(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, string command,
            string[] selectedTransactions,
            CancellationToken cancellationToken = default)
        {
            var derivationScheme = GetDerivationSchemeSettings(walletId);
            if (derivationScheme == null || derivationScheme.Network.ReadonlyWallet)
                return NotFound();

            switch (command)
            {
                case "cpfp":
                    {
                        selectedTransactions ??= Array.Empty<string>();
                        if (selectedTransactions.Length == 0)
                        {
                            TempData[WellKnownTempData.ErrorMessage] = $"No transaction selected";
                            return RedirectToAction(nameof(WalletTransactions), new { walletId });
                        }

                        var parameters = new MultiValueDictionary<string, string>();
                        parameters.Add("walletId", walletId.ToString());
                        int i = 0;
                        foreach (var tx in selectedTransactions)
                        {
                            parameters.Add($"transactionHashes[{i}]", tx);
                            i++;
                        }

                        var backUrl = Url.Action(nameof(WalletTransactions), new { walletId })!;
                        parameters.Add("returnUrl", backUrl);
                        parameters.Add("backUrl", backUrl);
                        return View("PostRedirect",
                            new PostRedirectViewModel
                            {
                                AspController = "UIWallets",
                                AspAction = nameof(WalletCPFP),
                                RouteParameters = { { "walletId", walletId.ToString() } },
                                FormParameters = parameters
                            });
                    }
                case "prune":
                    {
                        var result = await ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode)
                            .PruneAsync(derivationScheme.AccountDerivation, new PruneRequest(), cancellationToken);
                        if (result.TotalPruned == 0)
                        {
                            TempData[WellKnownTempData.SuccessMessage] = "The wallet is already pruned";
                        }
                        else
                        {
                            TempData[WellKnownTempData.SuccessMessage] =
                                $"The wallet has been successfully pruned ({result.TotalPruned} transactions have been removed from the history)";
                        }

                        return RedirectToAction(nameof(WalletTransactions), new { walletId });
                    }
                case "clear" when User.IsInRole(Roles.ServerAdmin):
                    {
                        if (Version.TryParse(_dashboard.Get(walletId.CryptoCode)?.Status?.Version ?? "0.0.0.0",
                                out var v) &&
                            v < new Version(2, 2, 4))
                        {
                            TempData[WellKnownTempData.ErrorMessage] =
                                "This version of NBXplorer doesn't support this operation, please upgrade to 2.2.4 or above";
                        }
                        else
                        {
                            await ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode)
                                .WipeAsync(derivationScheme.AccountDerivation, cancellationToken);
                            TempData[WellKnownTempData.SuccessMessage] =
                                "The transactions have been wiped out, to restore your balance, rescan the wallet.";
                        }

                        return RedirectToAction(nameof(WalletTransactions), new { walletId });
                    }
                default:
                    return NotFound();
            }
        }

        [HttpGet("{walletId}/export")]
        public async Task<IActionResult> Export(
            [ModelBinder(typeof(WalletIdModelBinder))] WalletId walletId,
            string format, string? labelFilter = null)
        {
            var paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var wallet = _walletProvider.GetWallet(paymentMethod.Network);
            var walletTransactionsInfoAsync = WalletRepository.GetWalletTransactionsInfo(walletId, (string[])null);
            var input = await wallet.FetchTransactionHistory(paymentMethod.AccountDerivation, null, null);
            var walletTransactionsInfo = await walletTransactionsInfoAsync;
            var export = new TransactionsExport(wallet, walletTransactionsInfo);
            var res = export.Process(input, format);

            var cd = new ContentDisposition
            {
                FileName = $"btcpay-{walletId}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{format}",
                Inline = true
            };
            Response.Headers.Add("Content-Disposition", cd.ToString());
            Response.Headers.Add("X-Content-Type-Options", "nosniff");
            return Content(res, "application/" + format);
        }

        private string GetImage(PaymentMethodId paymentMethodId, BTCPayNetwork network)
        {
            var res = paymentMethodId.PaymentType == PaymentTypes.BTCLike
                ? Url.Content(network.CryptoImagePath)
                : Url.Content(network.LightningImagePath);
            return Request.GetRelativePathOrAbsolute(res);
        }

        private string GetUserId() => _userManager.GetUserId(User);

        private StoreData GetCurrentStore() => HttpContext.GetStoreData();

        public IEnumerable<TransactionTagModel> CreateTransactionTagModels(WalletTransactionInfo? transactionInfo)
        {
            if (transactionInfo is null)
                return Array.Empty<TransactionTagModel>();

            string PayoutTooltip(IGrouping<string, string>? payoutsByPullPaymentId = null)
            {
                if (payoutsByPullPaymentId is null)
                {
                    return "Paid a payout";
                }
                else if (payoutsByPullPaymentId.Count() == 1)
                {
                    var pp = payoutsByPullPaymentId.Key;
                    var payout = payoutsByPullPaymentId.First();
                    if (!string.IsNullOrEmpty(pp))
                        return $"Paid a payout ({payout}) of a pull payment ({pp})";
                    else
                        return $"Paid a payout {payout}";
                }
                else
                {
                    var pp = payoutsByPullPaymentId.Key;
                    if (!string.IsNullOrEmpty(pp))
                        return $"Paid {payoutsByPullPaymentId.Count()} payouts of a pull payment ({pp})";
                    else
                        return $"Paid {payoutsByPullPaymentId.Count()} payouts";
                }
            }

            var models = new Dictionary<string, TransactionTagModel>();
            foreach (var tag in transactionInfo.Attachments)
            {
                if (models.ContainsKey(tag.Type))
                    continue;
                if (!transactionInfo.LabelColors.TryGetValue(tag.Type, out var color))
                    continue;
                var model = new TransactionTagModel
                {
                    Text = tag.Type,
                    Color = color,
                    TextColor = ColorPalette.Default.TextColor(color)
                };
                models.Add(tag.Type, model);
                if (tag.Type == WalletObjectData.Types.Payout)
                {
                    var payoutsByPullPaymentId =
                        transactionInfo.Attachments.Where(t => t.Type == "payout")
                        .GroupBy(t => t.Data?["pullPaymentId"]?.Value<string>() ?? "",
                                 k => k.Id).ToList();

                    model.Tooltip = payoutsByPullPaymentId.Count switch
                    {
                        0 => PayoutTooltip(),
                        1 => PayoutTooltip(payoutsByPullPaymentId.First()),
                        _ =>
                            $"<ul>{string.Join(string.Empty, payoutsByPullPaymentId.Select(pair => $"<li>{PayoutTooltip(pair)}</li>"))}</ul>"
                    };

                    model.Link = _linkGenerator.PayoutLink(transactionInfo.WalletId.ToString(), null, PayoutState.Completed, Request.Scheme, Request.Host,
                            Request.PathBase);
                }
                else if (tag.Type == WalletObjectData.Types.Payjoin)
                {
                    model.Tooltip = $"This UTXO was part of a PayJoin transaction.";
                }
                else if (tag.Type == WalletObjectData.Types.Invoice)
                {
                    model.Tooltip = $"Received through an invoice {tag.Id}";
                    model.Link = string.IsNullOrEmpty(tag.Id)
                            ? null
                            : _linkGenerator.InvoiceLink(tag.Id, Request.Scheme, Request.Host, Request.PathBase);
                }
                else if (tag.Type == WalletObjectData.Types.PaymentRequest)
                {
                    model.Tooltip = $"Received through a payment request {tag.Id}";
                    model.Link = _linkGenerator.PaymentRequestLink(tag.Id, Request.Scheme, Request.Host, Request.PathBase);
                }
                else if (tag.Type == WalletObjectData.Types.App)
                {
                    model.Tooltip = $"Received through an app {tag.Id}";
                    model.Link = _linkGenerator.AppLink(tag.Id, Request.Scheme, Request.Host, Request.PathBase);
                }
                else if (tag.Type == WalletObjectData.Types.PayjoinExposed)
                {

                    if (tag.Id.Length != 0)
                    {
                        model.Tooltip = $"This UTXO was exposed through a PayJoin proposal for an invoice ({tag.Id})";
                        model.Link = _linkGenerator.InvoiceLink(tag.Id, Request.Scheme, Request.Host, Request.PathBase);
                    }
                    else
                    {
                        model.Tooltip = $"This UTXO was exposed through a PayJoin proposal";
                    }
                }
                else if (tag.Type == WalletObjectData.Types.Payjoin)
                {
                    model.Tooltip = $"This UTXO was part of a PayJoin transaction.";
                }
                else
                {
                    model.Tooltip = tag.Data?.TryGetValue("tooltip", StringComparison.InvariantCultureIgnoreCase, out var tooltip) is true ? tooltip.ToString() : tag.Id;
                    if (tag.Data?.TryGetValue("link", StringComparison.InvariantCultureIgnoreCase, out var link) is true)
                    {
                        model.Link = link.ToString();
                    }
                }
            }
            foreach (var label in transactionInfo.LabelColors)
                models.TryAdd(label.Key, new TransactionTagModel
                {
                    Text = label.Key,
                    Color = label.Value,
                    TextColor = ColorPalette.Default.TextColor(label.Value)
                });
            return models.Values.OrderBy(v => v.Text);
        }
    }

    public class WalletReceiveViewModel
    {
        public string? CryptoImage { get; set; }
        public string? CryptoCode { get; set; }
        public string? Address { get; set; }
        public string? PaymentLink { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public class SendToAddressResult
    {
        [JsonProperty("psbt")] public string? PSBT { get; set; }
    }
}
