﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Microsoft.AppCenter.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using MoreLinq;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Controls;
using Wino.Controls.Advanced;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class MailListPage : MailListPageAbstract,
        IRecipient<ResetSingleMailItemSelectionEvent>,
        IRecipient<ClearMailSelectionsRequested>,
        IRecipient<ActiveMailItemChangedEvent>,
        IRecipient<ActiveMailFolderChangedEvent>,
        IRecipient<SelectMailItemContainerEvent>,
        IRecipient<ShellStateUpdated>,
        IRecipient<DisposeRenderingFrameRequested>
    {
        private const double RENDERING_COLUMN_MIN_WIDTH = 300;

        private IStatePersistanceService StatePersistenceService { get; } = App.Current.Services.GetService<IStatePersistanceService>();
        private IKeyPressService KeyPressService { get; } = App.Current.Services.GetService<IKeyPressService>();

        private MailItemDragPopup DragPopup = null;

        public MailListPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Bindings.Update();

            var windowContent = (Frame)Window.Current.Content;

            windowContent.AllowDrop = true;
            windowContent.DragOver += MailListView_DragOver;
            windowContent.Drop += root_Drop;

            // Here we add a new MailItemDragPopup to the overlay canvas in the AppShell.xaml container Page for more a aesthetically pleasing
            // and easier to work with drag+drop UI. We put it in the container page so that the drag popup control can be seen over
            // all other elements as well as go to any part of the page without being clipped by the bounds of this child page.
            //
            // TODO: In the future, create a better way to add things to the overlay canvas, maybe some sort of
            //       API so that more things can be added to the overlay like tooltips and popups.

            var overlayCanvas = ((AppShell)windowContent.Content).GetChildByName<Canvas>("ShellOverlayCanvas");
            DragPopup = new MailItemDragPopup() { Name = "DragPopupOverlay" };
            overlayCanvas.Children.Add(DragPopup);

            // Delegate to ViewModel.
            if (e.Parameter is NavigateMailFolderEventArgs folderNavigationArgs)
            {
                WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            var windowContent = (Frame)Window.Current.Content;

            windowContent.AllowDrop = false;
            windowContent.DragOver -= MailListView_DragOver;
            windowContent.Drop -= root_Drop;

            this.Bindings.StopTracking();

            RenderingFrame.Navigate(typeof(IdlePage));

            GC.Collect();
        }

        private void UpdateSelectAllButtonStatus()
        {
            // Check all checkbox if all is selected.
            // Unhook events to prevent selection overriding.

            SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;

            SelectAllCheckbox.IsChecked = MailListView.Items.Count > 0 && MailListView.SelectedItems.Count == MailListView.Items.Count;

            SelectAllCheckbox.Checked += SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked += SelectAllCheckboxUnchecked;
        }

        private void SelectionModeToggleChecked(object sender, RoutedEventArgs e)
        {
            ChangeSelectionMode(ListViewSelectionMode.Multiple);
        }

        private void FolderPivotChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var addedItem in e.AddedItems)
            {
                if (addedItem is FolderPivotViewModel pivotItem)
                {
                    pivotItem.IsSelected = true;
                }
            }

            foreach (var removedItem in e.RemovedItems)
            {
                if (removedItem is FolderPivotViewModel pivotItem)
                {
                    pivotItem.IsSelected = false;
                }
            }

            SelectAllCheckbox.IsChecked = false;
            SelectionModeToggle.IsChecked = false;

            MailListView.ClearSelections();

            UpdateSelectAllButtonStatus();
            ViewModel.SelectedPivotChangedCommand.Execute(null);
        }

        private void ChangeSelectionMode(ListViewSelectionMode mode)
        {
            MailListView.ChangeSelectionMode(mode);

            if (ViewModel?.PivotFolders != null)
            {
                ViewModel.PivotFolders.ForEach(a => a.IsExtendedMode = mode == ListViewSelectionMode.Extended);
            }
        }

        private void SelectionModeToggleUnchecked(object sender, RoutedEventArgs e)
        {
            ChangeSelectionMode(ListViewSelectionMode.Extended);
        }

        private void SelectAllCheckboxChecked(object sender, RoutedEventArgs e)
        {
            MailListView.SelectAllWino();
        }

        private void SelectAllCheckboxUnchecked(object sender, RoutedEventArgs e)
        {
            MailListView.ClearSelections();
        }

        void IRecipient<ResetSingleMailItemSelectionEvent>.Receive(ResetSingleMailItemSelectionEvent message)
        {
            // Single item in thread selected.
            // Force main list view to unselect all items, except for the one provided.

            MailListView.ClearSelections(message.SelectedViewModel);
        }

        private async void MailItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            // Context is requested from a single mail point, but we might have multiple selected items.
            // This menu should be calculated based on all selected items by providers.

            if (sender is MailItemDisplayInformationControl control && args.TryGetPosition(sender, out Point p))
            {
                await FocusManager.TryFocusAsync(control, FocusState.Keyboard);

                if (control.DataContext is IMailItem clickedMailItemContext)
                {
                    var targetItems = ViewModel.GetTargetMailItemViewModels(clickedMailItemContext);
                    var availableActions = ViewModel.GetAvailableMailActions(targetItems);

                    if (!availableActions?.Any() ?? false) return;
                    var t = targetItems.ElementAt(0);

                    ViewModel.ChangeCustomFocusedState(targetItems, true);

                    var clickedOperation = await GetMailOperationFromFlyoutAsync(availableActions, control, p.X, p.Y);

                    ViewModel.ChangeCustomFocusedState(targetItems, false);

                    if (clickedOperation == null) return;

                    var prepRequest = new MailOperationPreperationRequest(clickedOperation.Operation, targetItems.Select(a => a.MailCopy));

                    await ViewModel.ExecuteMailOperationAsync(prepRequest);
                }
            }
        }

        private async Task<MailOperationMenuItem> GetMailOperationFromFlyoutAsync(IEnumerable<MailOperationMenuItem> availableActions,
                                                                                  UIElement showAtElement,
                                                                                  double x,
                                                                                  double y)
        {
            var source = new TaskCompletionSource<MailOperationMenuItem>();

            var flyout = new MailOperationFlyout(availableActions, source);

            flyout.ShowAt(showAtElement, new FlyoutShowOptions()
            {
                ShowMode = FlyoutShowMode.Standard,
                Position = new Point(x + 30, y - 20)
            });

            return await source.Task;
        }

        void IRecipient<ClearMailSelectionsRequested>.Receive(ClearMailSelectionsRequested message)
        {
            MailListView.ClearSelections(null, preserveThreadExpanding: true);
        }

        void IRecipient<ActiveMailItemChangedEvent>.Receive(ActiveMailItemChangedEvent message)
        {
            // No active mail item. Go to empty page.
            if (message.SelectedMailItemViewModel == null)
            {
                WeakReferenceMessenger.Default.Send(new CancelRenderingContentRequested());
            }
            else
            {
                // Navigate to composing page.
                if (message.SelectedMailItemViewModel.IsDraft)
                {
                    NavigationTransitionType composerPageTransition = NavigationTransitionType.None;

                    // Dispose active rendering if there is any and go to composer.
                    if (IsRenderingPageActive())
                    {
                        // Prepare WebView2 animation from Rendering to Composing page.
                        PrepareRenderingPageWebViewTransition();

                        // Dispose existing HTML content from rendering page webview.
                        WeakReferenceMessenger.Default.Send(new CancelRenderingContentRequested());
                    }
                    else if (IsComposingPageActive())
                    {
                        // Composer is already active. Prepare composer WebView2 animation.
                        PrepareComposePageWebViewTransition();
                    }
                    else
                        composerPageTransition = NavigationTransitionType.DrillIn;

                    ViewModel.NavigationService.NavigateCompose(message.SelectedMailItemViewModel, composerPageTransition);
                }
                else
                {
                    // Find the MIME and go to rendering page.

                    if (message.SelectedMailItemViewModel == null) return;

                    if (IsComposingPageActive())
                    {
                        PrepareComposePageWebViewTransition();
                    }

                    ViewModel.NavigationService.NavigateRendering(message.SelectedMailItemViewModel);
                }
            }

            UpdateAdaptiveness();
        }

        private bool IsRenderingPageActive() => RenderingFrame.Content is MailRenderingPage;
        private bool IsComposingPageActive() => RenderingFrame.Content is ComposePage;

        private void PrepareComposePageWebViewTransition()
        {
            var webView = GetComposerPageWebView();

            if (webView != null)
            {
                var animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("WebViewConnectedAnimation", webView);
                animation.Configuration = new BasicConnectedAnimationConfiguration();
            }
        }

        private void PrepareRenderingPageWebViewTransition()
        {
            var webView = GetRenderingPageWebView();

            if (webView != null)
            {
                var animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("WebViewConnectedAnimation", webView);
                animation.Configuration = new BasicConnectedAnimationConfiguration();
            }
        }

        #region Connected Animation Helpers

        private WebView2 GetRenderingPageWebView()
        {
            if (RenderingFrame.Content is MailRenderingPage renderingPage)
                return renderingPage.GetWebView();

            return null;
        }

        private WebView2 GetComposerPageWebView()
        {
            if (RenderingFrame.Content is ComposePage composePage)
                return composePage.GetWebView();

            return null;
        }

        #endregion

        public void Receive(ActiveMailFolderChangedEvent message)
        {
            UpdateAdaptiveness();
        }

        public async void Receive(SelectMailItemContainerEvent message)
        {
            if (message.SelectedMailViewModel == null) return;

            await ViewModel.ExecuteUIThread(async () =>
            {
                MailListView.ClearSelections(message.SelectedMailViewModel, true);

                int retriedSelectionCount = 0;
            trySelection:

                bool isSelected = MailListView.SelectMailItemContainer(message.SelectedMailViewModel);

                if (!isSelected)
                {
                    for (int i = retriedSelectionCount; i < 5;)
                    {
                        // Retry with delay until the container is realized. Max 1 second.
                        await Task.Delay(200);

                        retriedSelectionCount++;

                        goto trySelection;
                    }
                }

                // Automatically scroll to the selected item.
                // This is useful when creating draft.
                if (isSelected && message.ScrollToItem)
                {
                    var collectionContainer = ViewModel.MailCollection.GetMailItemContainer(message.SelectedMailViewModel.UniqueId);

                    // Scroll to thread if available.
                    if (collectionContainer.ThreadViewModel != null)
                    {
                        MailListView.ScrollIntoView(collectionContainer.ThreadViewModel, ScrollIntoViewAlignment.Default);
                    }
                    else if (collectionContainer.ItemViewModel != null)
                    {
                        MailListView.ScrollIntoView(collectionContainer.ItemViewModel, ScrollIntoViewAlignment.Default);
                    }

                }
            });
        }

        public void Receive(ShellStateUpdated message)
        {
            UpdateAdaptiveness();
        }

        private void SearchBoxFocused(object sender, RoutedEventArgs e)
        {
            SearchBar.PlaceholderText = string.Empty;
        }

        private void SearchBarUnfocused(object sender, RoutedEventArgs e)
        {
            SearchBar.PlaceholderText = Translator.SearchBarPlaceholder;
        }

        private void ProcessMailItemKeyboardAccelerator(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
        {
            if (args.Key == Windows.System.VirtualKey.Delete)
            {
                args.Handled = true;

                ViewModel?.MailOperationCommand?.Execute((int)MailOperation.SoftDelete);
            }
        }

        /// <summary>
        /// Thread header is mail info display control and it can be dragged spearately out of ListView.
        /// We need to prepare a drag package for it from the items inside.
        /// </summary>
        private async void ThreadHeaderDragStart(UIElement sender, DragStartingEventArgs args)
        {
            if (sender is MailItemDisplayInformationControl control
                && control.ConnectedExpander?.Content is WinoListView contentListView)
            {
                var def = args.GetDeferral();

                var allItems = contentListView.Items.Where(a => a is IMailItem);

                // Highlight all items.
                allItems.Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = true);

                // Set native drag arg properties.
                args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

                var dragPackage = new MailDragPackage(allItems.Cast<IMailItem>());

                args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
                args.Data.Properties["DragPopup"] = DragPopup;

                var thumbnail = await ((MailItemDisplayInformationControl)sender).getThumbnailImageAsync();
                var img = await DragPopup.InitializeAndRenderDragPopupAsync(allItems.Cast<IMailItem>().ToArray(), thumbnail);
                args.DragUI.SetContentFromBitmapImage(img, new Point(0, 0));

                control.ConnectedExpander.IsExpanded = true;

                def.Complete();
            }
        }

        private void ThreadHeaderDragFinished(UIElement sender, DropCompletedEventArgs args)
        {
            if (sender is MailItemDisplayInformationControl control && control.ConnectedExpander != null && control.ConnectedExpander.Content is WinoListView contentListView)
            {
                contentListView.Items.Where(a => a is MailItemViewModel).Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = false);
            }
        }

        private async void LeftSwipeItemInvoked(Microsoft.UI.Xaml.Controls.SwipeItem sender, Microsoft.UI.Xaml.Controls.SwipeItemInvokedEventArgs args)
        {
            // Delete item for now.

            var swipeControl = args.SwipeControl;

            swipeControl.Close();

            if (swipeControl.Tag is MailItemViewModel mailItemViewModel)
            {
                var package = new MailOperationPreperationRequest(MailOperation.SoftDelete, mailItemViewModel.MailCopy);
                await ViewModel.ExecuteMailOperationAsync(package);
            }
            else if (swipeControl.Tag is ThreadMailItemViewModel threadMailItemViewModel)
            {
                var package = new MailOperationPreperationRequest(MailOperation.SoftDelete, threadMailItemViewModel.GetMailCopies());
                await ViewModel.ExecuteMailOperationAsync(package);
            }
        }

        private async void RightSwipeItemInvoked(Microsoft.UI.Xaml.Controls.SwipeItem sender, Microsoft.UI.Xaml.Controls.SwipeItemInvokedEventArgs args)
        {
            // Toggle status only for now.

            var swipeControl = args.SwipeControl;

            swipeControl.Close();

            if (swipeControl.Tag is MailItemViewModel mailItemViewModel)
            {
                var operation = mailItemViewModel.IsRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
                var package = new MailOperationPreperationRequest(operation, mailItemViewModel.MailCopy);

                await ViewModel.ExecuteMailOperationAsync(package);
            }
            else if (swipeControl.Tag is ThreadMailItemViewModel threadMailItemViewModel)
            {
                bool isAllRead = threadMailItemViewModel.ThreadItems.All(a => a.IsRead);

                var operation = isAllRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
                var package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.GetMailCopies());

                await ViewModel.ExecuteMailOperationAsync(package);
            }
        }

        private void PullToRefreshRequested(Microsoft.UI.Xaml.Controls.RefreshContainer sender, Microsoft.UI.Xaml.Controls.RefreshRequestedEventArgs args)
        {
            ViewModel.SyncFolderCommand?.Execute(null);
        }

        private async void SearchBar_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
            {
                await ViewModel.PerformSearchAsync();
            }
        }

        public void Receive(DisposeRenderingFrameRequested message)
        {
            ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
        }
        private void PageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewModel.MaxMailListLength = e.NewSize.Width - RENDERING_COLUMN_MIN_WIDTH;

            StatePersistenceService.IsReaderNarrowed = e.NewSize.Width < StatePersistenceService.MailListPaneLength + RENDERING_COLUMN_MIN_WIDTH;

            UpdateAdaptiveness();
        }

        private void MailListSizerManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            StatePersistenceService.MailListPaneLength = ViewModel.MailListLength;
        }

        private void UpdateAdaptiveness()
        {
            bool shouldDisplayNoMessagePanel, shouldDisplayMailingList, shouldDisplayRenderingFrame;

            bool isMultiSelectionEnabled = ViewModel.IsMultiSelectionModeEnabled || KeyPressService.IsCtrlKeyPressed();

            // This is the smallest state UI can get.
            // Either mailing list or rendering grid is visible.
            if (StatePersistenceService.IsReaderNarrowed)
            {
                // Start visibility checks by no message panel.
                shouldDisplayMailingList = isMultiSelectionEnabled ? true : (!ViewModel.HasSelectedItems || ViewModel.HasMultipleItemSelections);
                shouldDisplayNoMessagePanel = shouldDisplayMailingList ? false : !ViewModel.HasSelectedItems || ViewModel.HasMultipleItemSelections;
                shouldDisplayRenderingFrame = shouldDisplayMailingList ? false : !shouldDisplayNoMessagePanel;
            }
            else
            {
                shouldDisplayMailingList = true;
                shouldDisplayNoMessagePanel = !ViewModel.HasSelectedItems || ViewModel.HasMultipleItemSelections;
                shouldDisplayRenderingFrame = !shouldDisplayNoMessagePanel;
            }

            MailListContainer.Visibility = shouldDisplayMailingList ? Visibility.Visible : Visibility.Collapsed;
            RenderingFrame.Visibility = shouldDisplayRenderingFrame ? Visibility.Visible : Visibility.Collapsed;
            NoMailSelectedPanel.Visibility = shouldDisplayNoMessagePanel ? Visibility.Visible : Visibility.Collapsed;

            if (StatePersistenceService.IsReaderNarrowed == true)
            {
                if (ViewModel.HasSingleItemSelection && !isMultiSelectionEnabled)
                {
                    MailListColumn.Width = new GridLength(0);
                    RendererColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(MailListContainer, 0);
                    Grid.SetColumnSpan(RenderingGrid, 2);
                    MailListContainer.Visibility = Visibility.Collapsed;
                    RenderingGrid.Visibility = Visibility.Visible;
                }
                else
                {
                    MailListColumn.Width = new GridLength(1, GridUnitType.Star);
                    RendererColumn.Width = new GridLength(0);

                    Grid.SetColumnSpan(MailListContainer, 2);
                    MailListContainer.Margin = new Thickness(7, 0, 7, 0);
                    MailListContainer.Visibility = Visibility.Visible;
                    RenderingGrid.Visibility = Visibility.Collapsed;
                    SearchBar.Margin = new Thickness(8, 0, -2, 0);
                    MailListSizer.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                MailListColumn.Width = new GridLength(StatePersistenceService.MailListPaneLength);
                RendererColumn.Width = new GridLength(1, GridUnitType.Star);

                MailListContainer.Margin = new Thickness(0, 0, 0, 0);

                Grid.SetColumn(MailListContainer, 0);
                Grid.SetColumn(RenderingGrid, 1);
                Grid.SetColumnSpan(MailListContainer, 1);
                Grid.SetColumnSpan(RenderingGrid, 1);

                MailListContainer.Visibility = Visibility.Visible;
                RenderingGrid.Visibility = Visibility.Visible;
                SearchBar.Margin = new Thickness(2, 0, -2, 0);
                MailListSizer.Visibility = Visibility.Visible;
            }
        }

        private void MailItemDisplayInformationControl_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Because adding drag-events to the listview item prevents the default multiselection behavior
            // we have to manually implement them here :(
            if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            {
                if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) // Ctrl + Shift Click
                {
                    var targetItemIndex = MailListView.IndexFromContainer(MailListView.ContainerFromItem(((MailItemDisplayInformationControl)sender).DataContext));
                    MailListView.SelectRange(targetItemIndex, deselect: false);
                }
                else
                {
                    if (MailListView.SelectedItems.Contains(((MailItemDisplayInformationControl)sender).DataContext))
                    {
                        MailListView.SelectedItems.Remove(((MailItemDisplayInformationControl)sender).DataContext);
                    }
                    else
                    {
                        MailListView.SelectSingleItem((IMailItem)((MailItemDisplayInformationControl)sender).DataContext);
                    }
                }
            }
            else if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
            {
                var targetItemIndex = MailListView.IndexFromContainer(MailListView.ContainerFromItem(((MailItemDisplayInformationControl)sender).DataContext));
                MailListView.SelectRange(targetItemIndex);
            }
            else
            {
                MailListView.SelectedItems.Clear();
                MailListView.SelectSingleItem((IMailItem)((MailItemDisplayInformationControl)sender).DataContext);
            }
        }

        async private void MailItemDisplayInformationControl_DragStarting(UIElement sender, DragStartingEventArgs args)
        {
            var def = args.GetDeferral();
            var mailitem = (IMailItem)((MailItemDisplayInformationControl)sender).DataContext;

            MailDragPackage dragPackage;

            if (MailListView.SelectedItems.Contains(mailitem))
            {
                List<IMailItem> mailItems = new List<IMailItem>();
                foreach (IMailItem item in MailListView.SelectedItems)
                {
                    mailItems.Add(item);
                }
                dragPackage = new MailDragPackage(mailItems);
            }
            else
            {
                dragPackage = new MailDragPackage(mailitem);
            }

            args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
            args.Data.Properties.Add("DragPopup", DragPopup);

            // get all selected mail items
            IMailItem[] selectedItems;
            if (MailListView.SelectedItems.Contains(mailitem))
            {
                selectedItems = new IMailItem[MailListView.SelectedItems.Count];
                selectedItems = new IMailItem[MailListView.SelectedItems.Count];
                for (int i = 0; i < selectedItems.Length; i++)
                {
                    selectedItems[i] = MailListView.SelectedItems[i] as IMailItem;
                }
            }
            else
            {
                selectedItems = new IMailItem[] { mailitem };
            }

            // update dragui image
            var thumbnail = await (sender as MailItemDisplayInformationControl).getThumbnailImageAsync();
            var img = await DragPopup.InitializeAndRenderDragPopupAsync(selectedItems.ToArray(), thumbnail);
            args.DragUI.SetContentFromBitmapImage(img, new Point(0, 0));

            def.Complete();
        }

        private async void MailListView_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Properties.ContainsKey("DragPopup"))
            {
                // no logic is actually done here, all we do is hide the caption and glyph on the dragged over mail item
                var def = e.GetDeferral();

                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;

                // for some reason, image doesn't update when drag leaves folder
                // this fixes that by regenerating & applying image
                e.DragUIOverride.SetContentFromBitmapImage(await DragPopup.GenerateBitmap(), new Point(0, 0));

                def.Complete();
            }
        }

        private void root_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Properties.ContainsKey("DragPopup"))
            {
                if (e.Handled) return;
                var def = e.GetDeferral();

                DragPopup.MoveToMouse(e);
                DragPopup.Hide(MailItemDragPopup.HideAnimation.FadeOut);

                def.Complete();
            }
        }
    }
}
