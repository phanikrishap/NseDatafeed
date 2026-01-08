using System;
using System.Windows;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer;
using ZerodhaDatafeedAdapter.AddOns.TBSManager;
using ZerodhaDatafeedAdapter.AddOns.SimulationEngine;
using Logger = ZerodhaDatafeedAdapter.Logger;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// NinjaTrader AddOn that integrates Index Watch, Option Chain, and TBS Manager into the Control Center menu
    /// and auto-launches windows on startup.
    /// </summary>
    public class MarketAnalyzerAddOn : AddOnBase
    {
        private NTMenuItem _menuItem;
        private NTMenuItem _tbsMenuItem;
        private NTMenuItem _simMenuItem;
        private NTMenuItem _existingNewMenu;
        private static bool _autoOpened = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Zerodha Adapter AddOns";
                Name = "MarketAnalyzerAddOn";
                Logger.Info("[MarketAnalyzerAddOn] OnStateChange(): SetDefaults - AddOn initialized");
            }
            else if (State == State.Terminated)
            {
                Logger.Info("[MarketAnalyzerAddOn] OnStateChange(): Terminated");
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            Logger.Debug($"[MarketAnalyzerAddOn] OnWindowCreated(): Window type = {window.GetType().Name}");

            var controlCenter = window as ControlCenter;
            if (controlCenter == null)
            {
                Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Not a ControlCenter window, skipping");
                return;
            }

            Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): ControlCenter detected");

            // Auto-Launch Logic - only on first ControlCenter creation
            if (!_autoOpened)
            {
                _autoOpened = true;
                Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Auto-launching windows...");

                Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Open Index Watch window (formerly GIFT NIFTY Market Analyzer)
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating IndexWatch (MarketAnalyzerWindow) instance");
                        var win = new MarketAnalyzerWindow();
                        win.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): IndexWatch shown successfully");

                        // Open Option Chain window
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating OptionChainWindow instance");
                        var chainWin = new OptionChainWindow();
                        chainWin.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): OptionChainWindow shown successfully");

                        // Open TBS Manager window
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating TBSManagerWindow instance");
                        var tbsWin = new TBSManagerWindow();
                        tbsWin.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): TBSManagerWindow shown successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MarketAnalyzerAddOn] OnWindowCreated(): Failed to create window - {ex.Message}", ex);
                    }
                }));
            }

            // Menu Integration
            Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Setting up menu integration");
            _existingNewMenu = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;

            if (_existingNewMenu == null)
            {
                Logger.Warn("[MarketAnalyzerAddOn] OnWindowCreated(): Could not find ControlCenterMenuItemNew - menu integration skipped");
                return;
            }

            _menuItem = new NTMenuItem
            {
                Header = "Index Watch",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _menuItem.Click += OnMenuItemClick;
            _existingNewMenu.Items.Add(_menuItem);

            // Add TBS Manager menu item
            _tbsMenuItem = new NTMenuItem
            {
                Header = "TBS Manager",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _tbsMenuItem.Click += OnTBSMenuItemClick;
            _existingNewMenu.Items.Add(_tbsMenuItem);

            // Add Simulation Engine menu item (does NOT auto-launch)
            _simMenuItem = new NTMenuItem
            {
                Header = "Simulation Engine",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _simMenuItem.Click += OnSimMenuItemClick;
            _existingNewMenu.Items.Add(_simMenuItem);

            Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Menu items added successfully");
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window is ControlCenter)
            {
                Logger.Info("[MarketAnalyzerAddOn] OnWindowDestroyed(): ControlCenter closing, cleaning up menu items");

                if (_existingNewMenu != null)
                {
                    if (_menuItem != null && _existingNewMenu.Items.Contains(_menuItem))
                    {
                        _existingNewMenu.Items.Remove(_menuItem);
                        _menuItem.Click -= OnMenuItemClick;
                        _menuItem = null;
                    }

                    if (_tbsMenuItem != null && _existingNewMenu.Items.Contains(_tbsMenuItem))
                    {
                        _existingNewMenu.Items.Remove(_tbsMenuItem);
                        _tbsMenuItem.Click -= OnTBSMenuItemClick;
                        _tbsMenuItem = null;
                    }

                    if (_simMenuItem != null && _existingNewMenu.Items.Contains(_simMenuItem))
                    {
                        _existingNewMenu.Items.Remove(_simMenuItem);
                        _simMenuItem.Click -= OnSimMenuItemClick;
                        _simMenuItem = null;
                    }
                }

                Logger.Info("[MarketAnalyzerAddOn] OnWindowDestroyed(): Menu item cleanup complete");
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnMenuItemClick(): User clicked menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new MarketAnalyzerWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnMenuItemClick(): New MarketAnalyzerWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnMenuItemClick(): Failed to create window - {ex.Message}", ex);
                }
            }));
        }

        private void OnTBSMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnTBSMenuItemClick(): User clicked TBS Manager menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new TBSManagerWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnTBSMenuItemClick(): New TBSManagerWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnTBSMenuItemClick(): Failed to create window - {ex.Message}", ex);
                }
            }));
        }

        private void OnSimMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnSimMenuItemClick(): User clicked Simulation Engine menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new SimulationEngineWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnSimMenuItemClick(): New SimulationEngineWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnSimMenuItemClick(): Failed to create window - {ex.Message}", ex);
                }
            }));
        }
    }
}
