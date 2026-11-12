using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.BackendApi;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using RaidZero.Shared.Trading;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// The Hideout's trader: a third column next to the stash, opened from the player bar. Buy lists what the
    /// trader sells, with stock and price; Sell is a list you drag items onto (or right-click them) and sell in
    /// one go. Listed items stay where they are, marked, until the sale goes through. Prices come from the
    /// shared rules (TraderPrices, TradePlanner) with the trader's own share, so what's shown is what's paid; the
    /// backend checks everything again. Item tooltips show what the trader would pay while it's open.
    /// </summary>
    internal sealed class TraderPanel
    {
        /// <summary>The most of one offer a purchase can ask for (TraderList.MaxCount on the backend).</summary>
        private const int MaxCount = 20;

        // The offer's picture: its footprint, scaled down to fit this box, in panel units.
        private const float IconWidth = 112f;
        private const float IconHeight = 56f;

        private readonly InventoryView _view;
        private readonly VisualElement _column;
        private readonly Label _title;
        private readonly Label _money;
        private readonly Button _buyTab;
        private readonly Button _sellTab;
        private readonly ScrollView _buyPage;
        private readonly VisualElement _sellPage;
        private readonly VisualElement _sellZone;
        private readonly Label _sellHint;
        private readonly ScrollView _sellRows;
        private readonly Label _sellTotal;
        private readonly Button _sellButton;
        private readonly Button _clearButton;
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        private PlayerSession _session;
        private ItemCatalog _catalog;
        private TraderSellList _selling;
        private TraderInfo _trader;
        private string _loadedFor;
        private bool _onSellPage;
        private bool _busy;
        private bool _loading;
        private float _nextTick;

        public TraderPanel(InventoryView view)
        {
            _view = view;
            _column = new VisualElement().WithClass("inv-column").WithClass("trader");
            _column.style.display = DisplayStyle.None;

            var panel = new VisualElement().WithClass("inv-panel").WithClass("trader__panel");
            var header = new VisualElement().WithClass("inv-panel__header");
            _title = new Label("Trader").WithClass("inv-panel__title");
            header.Add(_title);
            header.Add(new Button(Close) { text = "×", tooltip = "Close" }.WithClass("inv-panel__close"));
            panel.Add(header);

            var moneyRow = new VisualElement().WithClass("trader__money-row");
            moneyRow.Add(new Label("Your money").WithClass("trader__money-label"));
            _money = new Label().WithClass("trader__money");
            moneyRow.Add(_money);
            panel.Add(moneyRow);

            var tabs = new VisualElement().WithClass("trader__tabs");
            _buyTab = new Button(() => ShowPage(sell: false)) { text = "Buy" }.WithClass("trader__tab");
            _sellTab = new Button(() => ShowPage(sell: true)) { text = "Sell" }.WithClass("trader__tab");
            tabs.Add(_buyTab);
            tabs.Add(_sellTab);
            panel.Add(tabs);

            _buyPage = new ScrollView(ScrollViewMode.Vertical).WithClass("trader__page");
            panel.Add(_buyPage);

            _sellPage = new VisualElement().WithClass("trader__page");
            _sellZone = new VisualElement
            {
                userData = new DropZoneRef { Check = WhyNotSell, Dropped = id => List(id) },
            }.WithClass("trader__sell-zone");
            _sellHint = new Label("Drag items here, or right-click them, to sell them. They stay where they are until you press Sell.").WithClass("trader__hint");
            _sellRows = new ScrollView(ScrollViewMode.Vertical).WithClass("trader__sell-rows");
            _sellZone.Add(_sellHint);
            _sellZone.Add(_sellRows);
            _sellPage.Add(_sellZone);

            var footer = new VisualElement().WithClass("trader__sell-footer");
            _sellTotal = new Label().WithClass("trader__sell-total");
            _clearButton = new Button(() => _selling?.Clear()) { text = "Clear" }.WithClass("hideout-button");
            _sellButton = new Button(Sell) { text = "Sell" }.WithClass("hideout-button").WithClass("trader__sell-button");
            footer.Add(_sellTotal);
            footer.Add(_clearButton);
            footer.Add(_sellButton);
            _sellPage.Add(footer);
            panel.Add(_sellPage);

            _column.Add(panel);
            _view.Columns.Add(_column);
            _view.ItemRightClicked += OnItemRightClicked;
            _view.TooltipExtraLine = PriceLine;
            ShowPage(sell: false);
        }

        /// <summary>Something the Hideout draws changed (the sell list, so its marks): draw again.</summary>
        public event Action NeedsRedraw;

        public bool IsOpen { get; private set; }

        /// <summary>The player's session and the catalog, or nulls when there's none (logged out, or a new backend).</summary>
        public void SetSession(PlayerSession session, ItemCatalog catalog)
        {
            if (_selling != null)
            {
                _selling.Changed -= OnSellListChanged;
            }

            _session = session;
            _catalog = catalog;
            _trader = null;
            _loadedFor = null;
            _selling = catalog != null ? new TraderSellList(catalog) : null;
            if (_selling != null)
            {
                _selling.Changed += OnSellListChanged;
            }

            _counts.Clear();
            if (IsOpen)
            {
                Load();
            }
        }

        public void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            IsOpen = true;
            _column.style.display = DisplayStyle.Flex;
            Load();
            NeedsRedraw?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            _column.style.display = DisplayStyle.None;
            _selling?.Clear();
            NeedsRedraw?.Invoke();
        }

        /// <summary>Draws it again from the profile as it is now. The Hideout calls it after rebuilding the panels.</summary>
        public void Redraw()
        {
            if (!IsOpen)
            {
                return;
            }

            var profile = Profile;
            if (profile != null && profile.PlayerId != _loadedFor && !_loading)
            {
                Load(); // logged in as someone else: their stock, not the last player's
            }

            _selling?.Prune(profile, BuysAt);
            _title.text = _trader != null ? _trader.DisplayName : _loading ? "Trader" : "Trader (not reachable)";
            _money.text = profile != null ? TraderText.Credits(TradePlanner.Money(profile.Stash)) : "";
            DrawOffers(profile);
            DrawSellList(profile);

            // Listed items, marked where they lie.
            foreach (var id in _selling?.ItemIds ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                var element = _view.Panels.FindItem(id);
                element?.AddToClassList("inv-item--selling");
            }
        }

        /// <summary>Every frame while the Hideout shows: keeps the restock times current, and reloads when one passes.</summary>
        public void Tick()
        {
            if (!IsOpen || Time.unscaledTime < _nextTick)
            {
                return;
            }

            _nextTick = Time.unscaledTime + 15f;
            if (_trader == null || _loading || _busy)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_trader.Offers.Any(o => o.RestocksAt.HasValue && o.RestocksAt.Value <= now && o.Left < o.Stock))
            {
                Load();
            }
            else if (_trader.Offers.Any(o => o.Left == 0))
            {
                NeedsRedraw?.Invoke(); // a "more in" countdown to bring up to date
            }
        }

        private PlayerProfile Profile => _session != null && _session.IsLoggedIn ? _session.Profile : null;

        private double BuysAt => _trader?.BuysAt ?? 0;

        private void ShowPage(bool sell)
        {
            _onSellPage = sell;
            _buyPage.style.display = sell ? DisplayStyle.None : DisplayStyle.Flex;
            _sellPage.style.display = sell ? DisplayStyle.Flex : DisplayStyle.None;
            _buyTab.EnableInClassList("trader__tab--on", !sell);
            _sellTab.EnableInClassList("trader__tab--on", sell);
        }

        private void OnSellListChanged() => NeedsRedraw?.Invoke();

        // ---- Loading ----

        private async void Load()
        {
            if (_session == null || !_session.IsLoggedIn || _loading)
            {
                return;
            }

            _loading = true;
            _loadedFor = _session.Profile?.PlayerId; // even if it fails: Refresh or reopening tries again
            NeedsRedraw?.Invoke();
            try
            {
                var session = _session;
                var result = await session.GetTradersAsync();
                if (session != _session)
                {
                    return; // logged out or a new session meanwhile
                }

                if (!result.Succeeded)
                {
                    _view.ShowMessage("Couldn't reach the trader: " + InventoryUiErrors.Describe(result.Error));
                    return;
                }

                _trader = result.Value.FirstOrDefault();
                if (_trader == null)
                {
                    _view.ShowMessage("The backend has no traders set up.");
                }
            }
            catch (Exception exception)
            {
                _view.ShowMessage("Couldn't reach the trader: " + exception.Message);
            }
            finally
            {
                _loading = false;
                NeedsRedraw?.Invoke();
            }
        }

        // ---- Buying ----

        private void DrawOffers(PlayerProfile profile)
        {
            _buyPage.Clear();
            if (_trader == null)
            {
                _buyPage.Add(new Label(_loading ? "Loading..." : "Nothing to show.").WithClass("inv-empty"));
                return;
            }

            long money = profile != null ? TradePlanner.Money(profile.Stash) : 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var offer in _trader.Offers)
            {
                _buyPage.Add(OfferRow(offer, profile, money, now));
            }
        }

        private VisualElement OfferRow(TraderOfferInfo offer, PlayerProfile profile, long money, DateTimeOffset now)
        {
            int most = Math.Max(1, Math.Min(MaxCount, offer.Left ?? MaxCount));
            int count = Math.Min(_counts.TryGetValue(offer.OfferId, out var c) ? c : 1, most);
            long cost = offer.Price * count;
            bool soldOut = offer.Left.HasValue && offer.Left.Value <= 0;
            bool affordable = money >= cost;

            var row = new VisualElement().WithClass("trader-offer");
            row.EnableInClassList("trader-offer--sold-out", soldOut);
            row.Add(Icon(offer.Item));

            var info = new VisualElement().WithClass("trader-offer__info");
            info.Add(new Label(TraderText.Title(_catalog, offer.Item)).WithClass("trader-offer__name"));
            var contents = TraderText.Contents(_catalog, offer.Item);
            if (contents != null)
            {
                info.Add(new Label(contents).WithClass("trader-offer__contents"));
            }

            var stock = TraderText.Stock(offer, now);
            if (stock != null)
            {
                info.Add(new Label(stock).WithClass("trader-offer__stock"));
            }

            row.Add(info);

            var buy = new VisualElement().WithClass("trader-offer__buy");
            buy.Add(new Label(TraderText.Credits(cost)).WithClass("trader-offer__price").WithClassIf("trader-offer__price--short", !affordable && !soldOut));
            var controls = new VisualElement().WithClass("trader-offer__controls");
            if (most > 1)
            {
                controls.Add(new Button(() => SetCount(offer, count - 1)) { text = "−" }.WithClass("trader-offer__step"));
                controls.Add(new Label(count.ToString()).WithClass("trader-offer__count"));
                controls.Add(new Button(() => SetCount(offer, count + 1)) { text = "+" }.WithClass("trader-offer__step"));
            }

            var button = new Button(() => Buy(offer, count)) { text = "Buy" }.WithClass("hideout-button").WithClass("trader-offer__button");
            button.SetEnabled(!_busy && profile != null && !profile.IsInRaid && !soldOut && affordable);
            controls.Add(button);
            buy.Add(controls);
            row.Add(buy);
            return row;
        }

        // The offer's item drawn like it is in a grid, scaled down to fit the icon box.
        private VisualElement Icon(ItemNode item)
        {
            var box = new VisualElement().WithClass("trader-offer__icon");
            box.style.width = IconWidth;
            box.style.height = IconHeight;
            if (_catalog == null || item == null || !_catalog.TryGet(item.DefId, out var def))
            {
                return box;
            }

            GridPlacement.GetSize(def, false, out int w, out int h);
            float scale = Mathf.Min(1f, Mathf.Min(IconWidth / (w * InventoryPanels.Cell), IconHeight / (h * InventoryPanels.Cell)));
            float width = w * InventoryPanels.Cell * scale;
            float height = h * InventoryPanels.Cell * scale;
            var look = new VisualElement { pickingMode = PickingMode.Ignore };
            InventoryItemLook.Fill(look, _catalog, item, def, false, width, height, ItemBadges.Count(_catalog, item));
            look.style.width = width;
            look.style.height = height;
            box.Add(look);
            return box;
        }

        private void SetCount(TraderOfferInfo offer, int count)
        {
            _counts[offer.OfferId] = Math.Max(1, Math.Min(MaxCount, count));
            NeedsRedraw?.Invoke();
        }

        private async void Buy(TraderOfferInfo offer, int count)
        {
            if (_busy || _session == null || _trader == null)
            {
                return;
            }

            _busy = true;
            NeedsRedraw?.Invoke();
            try
            {
                var outcome = await _session.BuyAsync(_trader.TraderId, offer.OfferId, count);
                if (outcome.Succeeded)
                {
                    _counts.Remove(offer.OfferId);
                    _view.ShowMessage("Bought " + TraderText.Title(_catalog, offer.Item) + (count > 1 ? " ×" + count : "") + " for " + TraderText.Credits(outcome.Credits) + ". It's in your stash.");
                }
                else
                {
                    _view.ShowMessage(TraderText.Refusal(outcome));
                }
            }
            catch (Exception exception)
            {
                _view.ShowMessage("The purchase didn't go through: " + exception.Message);
            }
            finally
            {
                _busy = false;
                Load(); // what's left of each offer
            }
        }

        // ---- Selling ----

        private void DrawSellList(PlayerProfile profile)
        {
            _sellRows.Clear();
            var ids = _selling?.ItemIds ?? (IReadOnlyList<string>)Array.Empty<string>();
            _sellHint.style.display = ids.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var id in ids)
            {
                var item = TraderSellList.Find(profile, id);
                if (item == null)
                {
                    continue;
                }

                var row = new VisualElement().WithClass("trader-sell");
                var name = TraderText.Title(_catalog, item) + (item.Children.Count > 0 ? " and what's in it" : "");
                row.Add(new Label(name).WithClass("trader-sell__name"));
                row.Add(new Label(TraderText.Credits(_selling.PriceOf(profile, BuysAt, id))).WithClass("trader-sell__price"));
                var itemId = id;
                row.Add(new Button(() => _selling.Remove(itemId)) { text = "×", tooltip = "Keep it" }.WithClass("inv-panel__close"));
                _sellRows.Add(row);
            }

            long total = _selling != null ? _selling.Total(profile, BuysAt) : 0;
            _sellTotal.text = ids.Count == 0 ? "" : "Total: " + TraderText.Credits(total);
            _sellButton.text = ids.Count == 0 ? "Sell" : "Sell for " + TraderText.Credits(total);
            _sellButton.SetEnabled(!_busy && ids.Count > 0 && profile != null && !profile.IsInRaid && _trader != null);
            _clearButton.SetEnabled(!_busy && ids.Count > 0);
        }

        private string WhyNotSell(string itemId)
        {
            if (_trader == null || _selling == null)
            {
                return "The trader isn't here right now.";
            }

            if (Profile != null && Profile.IsInRaid)
            {
                return "Your gear is locked while you're in a raid.";
            }

            return _selling.WhyNot(Profile, BuysAt, itemId);
        }

        private void List(string itemId)
        {
            var problem = WhyNotSell(itemId) ?? _selling.Add(Profile, BuysAt, itemId);
            if (problem != null)
            {
                _view.ShowMessage(problem);
            }
        }

        // Right-click lists an item, or takes it off the list, while the trader is open.
        private void OnItemRightClicked(string itemId)
        {
            if (!IsOpen || _selling == null || _busy)
            {
                return;
            }

            ShowPage(sell: true);
            if (_selling.Contains(itemId))
            {
                _selling.Remove(itemId);
            }
            else
            {
                List(itemId);
            }
        }

        private async void Sell()
        {
            if (_busy || _session == null || _trader == null || _selling == null || _selling.Count == 0)
            {
                return;
            }

            _busy = true;
            NeedsRedraw?.Invoke();
            try
            {
                var ids = _selling.ItemIds.ToList();
                var outcome = await _session.SellAsync(_trader.TraderId, ids);
                if (outcome.Succeeded)
                {
                    _selling.Clear();
                    _view.ShowMessage("Sold " + (ids.Count == 1 ? "it" : ids.Count + " items") + " for " + TraderText.Credits(outcome.Credits) + ".");
                }
                else
                {
                    _view.ShowMessage(TraderText.Refusal(outcome));
                }
            }
            catch (Exception exception)
            {
                _view.ShowMessage("The sale didn't go through: " + exception.Message);
            }
            finally
            {
                _busy = false;
                NeedsRedraw?.Invoke();
            }
        }

        // The tooltip's last line while the trader is open: what they'd pay for the item and what's in it.
        private TooltipLine PriceLine(ItemNode item)
        {
            if (!IsOpen || _trader == null || _catalog == null || item == null || item.DefId == WellKnownDefs.Credits)
            {
                return null;
            }

            long price = TraderPrices.SellPrice(_catalog, item, _trader.BuysAt);
            return price > 0 ? new TooltipLine(_trader.DisplayName + " pays", TraderText.Credits(price)) : new TooltipLine(_trader.DisplayName, "won't buy it");
        }
    }

    internal static class TraderElementExtensions
    {
        public static T WithClassIf<T>(this T element, string className, bool on) where T : VisualElement
        {
            element.EnableInClassList(className, on);
            return element;
        }
    }
}
