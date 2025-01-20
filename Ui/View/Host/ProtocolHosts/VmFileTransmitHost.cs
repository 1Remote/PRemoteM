using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using _1RM.Model;
using _1RM.Model.Protocol.FileTransmit;
using _1RM.Model.Protocol.FileTransmit.Transmitters;
using _1RM.Model.Protocol.FileTransmit.Transmitters.TransmissionController;
using _1RM.Service;
using _1RM.Service.Locality;
using _1RM.Utils;
using Dapper;
using Shawn.Utils;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.FileSystem;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MessageBox = System.Windows.MessageBox;

namespace _1RM.View.Host.ProtocolHosts
{
    public partial class VmFileTransmitHost : NotifyPropertyChangedBase
    {
        public readonly string ConnectionId;
        public VmFileTransmitHost(IFileTransmittable protocol, string connectionId)
        {
            _protocol = protocol;
            ConnectionId = connectionId;
        }

        ~VmFileTransmitHost()
        {
            _consumingTransmitTaskCancellationTokenSource.Cancel(false);
        }

        public void Release()
        {
            foreach (var t in TransmitTasks)
            {
                t.TryCancel();
            }
            Trans?.Release();
        }

        public void Conn()
        {
            if (Trans?.IsConnected() != true)
            {
                GridLoadingVisibility = Visibility.Visible;
                var task = new Task(() =>
                {
                    try
                    {
                        Trans = _protocol.GeTransmitter();
                        if (!string.IsNullOrWhiteSpace(_protocol.GetStartupPath()))
                            ShowFolder(_protocol.GetStartupPath());
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                        IoMessageLevel = IoMessageLevelError;
                        IoMessage = e.Message;
                    }
                    finally
                    {
                        GridLoadingVisibility = Visibility.Collapsed;
                    }
                }, _consumingTransmitTaskCancellationTokenSource.Token);
                task.Start();
            }
            else
                GridLoadingVisibility = Visibility.Collapsed;
        }

        private void AddTransmitTask(TransmitTask t)
        {
            TransmitTasks.Insert(0, t);
            void func(ETransmitTaskStatus status, Exception? e)
            {
                if (t.OnTaskEnd != null)
                    t.OnTaskEnd -= func;

                if (e != null)
                {
                    IoMessageLevel = IoMessageLevelError;
                    IoMessage = e.Message;
                }

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        if (Application.Current == null)
                            return;
                        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
                        SynchronizationContext.Current?.Post(pl =>
                        {
                            try
                            {
                                // refresh after transmitted
                                if (t.ItemsHaveBeenTransmitted.Any(x =>
                                                                            x.TransmissionType == ETransmissionType.HostToServer
                                                                        && x.DstPath.Contains(CurrentPath)))
                                {
                                    CmdGoToPathCurrent.Execute();
                                }
                                if (t.TransmitTaskStatus == ETransmitTaskStatus.Cancel
                                && TransmitTasks.Contains(t))
                                    TransmitTasks.Remove(t);
                            }
                            catch (Exception e1)
                            {
                                SimpleLogHelper.Error(e1);
                            }
                        }, null);
                    }
                    catch (Exception e2)
                    {
                        SimpleLogHelper.Error(e2);
                    }
                });
            }

            t.OnTaskEnd += func;
        }

        private int _remoteItemsOrderBy = -1;

        /// <summary>
        /// -1: by name asc(default)
        /// 0: by name asc
        /// 1: by name desc
        /// 2: by size asc
        /// 3: by size desc
        /// 4: by LastUpdate asc
        /// 5: by LastUpdate desc
        /// 6: by FileType asc
        /// 7: by FileType desc
        /// </summary>
        public int RemoteItemsOrderBy
        {
            get => _remoteItemsOrderBy;
            set
            {
                if (_remoteItemsOrderBy != value)
                {
                    SetAndNotifyIfChanged(ref _remoteItemsOrderBy, value);
                    MakeRemoteItemsOrderBy();
                }
            }
        }

        private void MakeRemoteItemsOrderBy()
        {
            if (RemoteItems?.Count > 0)
            {
                foreach (var remoteItem in RemoteItems)
                {
                    remoteItem.IsSelected = false;
                }

                var remoteItemInfos = RemoteItemsOrderBy switch
                {
                    -1 => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, int>(a => a.IsDirectory ? 1 : 2).ThenBy(x => x.Name)),
                    0 => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, string>(x => x.Name)),
                    1 => new ObservableCollection<RemoteItem>(RemoteItems.OrderByDescending<RemoteItem, string>(x => x.Name)),
                    2 => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, ulong>(x => x.ByteSize)),
                    3 => new ObservableCollection<RemoteItem>(RemoteItems.OrderByDescending<RemoteItem, ulong>(x => x.ByteSize)),
                    4 => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, DateTime>(x => x.LastUpdate)),
                    5 => new ObservableCollection<RemoteItem>(RemoteItems.OrderByDescending<RemoteItem, DateTime>(x => x.LastUpdate)),
                    6 => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, string>(x => x.FileType).ThenBy(x => x.Name)),
                    7 => new ObservableCollection<RemoteItem>(RemoteItems.OrderByDescending<RemoteItem, string>(x => x.FileType).ThenBy(x => x.Name)),
                    _ => new ObservableCollection<RemoteItem>(RemoteItems.OrderBy<RemoteItem, int>(a => a.IsDirectory ? 1 : 2).ThenBy(x => x.Name))
                };
                RemoteItems = remoteItemInfos;
            }
        }

        /// <summary>
        /// mode = 1 go preview, mode = 2 go following
        /// will not remember pathHistory when mode != 0
        /// </summary>
        /// <param name="path"></param>
        /// <param name="mode"></param>
        /// <param name="showIoMessage"></param>
        private void ShowFolder(string path, int mode = 0, bool showIoMessage = true)
        {
            var t = new Task(async () =>
            {
                GridLoadingVisibility = Visibility.Visible;
                try
                {
                    //SimpleLogHelper.Debug($"ShowFolder({path}, {mode}) START");
                    if (string.IsNullOrWhiteSpace(path))
                        path = "/";
                    if (path.EndsWith("/.."))
                    {
                        //SimpleLogHelper.Debug($"ShowFolder after path.EndsWith(/..)");
                        path = path.Substring(0, path.Length - 3);
                        var i = path.LastIndexOf("/", StringComparison.Ordinal);
                        if (i > 0)
                        {
                            path = path.Substring(0, i);
                        }
                    }

                    try
                    {
                        var remoteItemInfos = new ObservableCollection<RemoteItem>();
                        //SimpleLogHelper.Debug($"ShowFolder before ListDirectoryItems");
                        var items = await Trans.ListDirectoryItems(path);
                        if (Enumerable.Any<RemoteItem>(items))
                        {
                            remoteItemInfos = new ObservableCollection<RemoteItem>(items);
                        }

                        RemoteItems = remoteItemInfos;
                        //SimpleLogHelper.Debug($"ShowFolder before MakeRemoteItemsOrderBy");
                        MakeRemoteItemsOrderBy();

                        if (path != CurrentPath)
                        {
                            if (mode == 0
                                && (_pathHistoryPrevious.Count == 0 || _pathHistoryPrevious.Peek() != CurrentPath))
                            {
                                _pathHistoryPrevious.Push(CurrentPath);
                                if (_pathHistoryFollowing.Count > 0 &&
                                    _pathHistoryFollowing.Peek() != path)
                                    _pathHistoryFollowing.Clear();
                            }

                            CurrentPath = path;
                        }

                        if (CurrentPath != "/" && CurrentPath != "")
                            CmdGoToPathParentEnable = true;
                        else
                            CmdGoToPathParentEnable = false;

                        CmdGoToPathPreviousEnable = _pathHistoryPrevious.Count > 0;
                        CmdGoToPathFollowingEnable = _pathHistoryFollowing.Count > 0;

                        if (showIoMessage)
                        {
                            IoMessageLevel = IoMessageLevelNormal;
                            IoMessage = $"ls {CurrentPath}";
                        }
                    }
                    catch (Exception e)
                    {
                        IoMessageLevel = IoMessageLevelError;
                        IoMessage = $"ls {CurrentPath}: " + e.Message;
                        if (CurrentPath != path)
                            ShowFolder(CurrentPath, showIoMessage: false);
                        return;
                    }
                }
                finally
                {
                    GridLoadingVisibility = Visibility.Collapsed;
                }
                //SimpleLogHelper.Debug($"ShowFolder({path}, {mode}) END");
            });
            t.Start();
        }

        private async void DeleteSelectedItems()
        {
            foreach (var itemInfo in RemoteItems)
            {
                if (itemInfo.IsSelected)
                {
                    var selected = itemInfo.FullName;
                    try
                    {
                        await Trans.Delete(selected);
                        IoMessageLevel = IoMessageLevelNormal;
                        IoMessage = $"Delete {selected}";
                    }
                    catch (Exception e)
                    {
                        IoMessageLevel = IoMessageLevelError;
                        IoMessage = $"Delete {selected}: " + e.Message;
                    }
                }
            }
            ShowFolder(CurrentPath, showIoMessage: false);
        }

        public void FileList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ListView? view = null;
            ScrollContentPresenter? p = null;
            if (sender is ListView lv)
            {
                view = lv;
                var ip = MyVisualTreeHelper.FindVisualChild<ItemsPresenter>(view);
                p = MyVisualTreeHelper.VisualUpwardSearch<ScrollContentPresenter>((DependencyObject)ip!);
            }
            if (view == null || p == null)
                return;

            var aMenu = new System.Windows.Controls.ContextMenu();
            {
                var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_refresh") };
                menu.Click += (o, a) =>
                {
                    CmdGoToPathCurrent.Execute();
                };
                aMenu.Items.Add(menu);
            }
            {
                var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_create_folder") };
                menu.Click += (o, a) =>
                {
                    CmdEndRenaming.Execute();
                    var newFolder = new RemoteItem()
                    {
                        IsRenaming = true,
                        Name = "New Folder",
                        FullName = "",
                        IsDirectory = true,
                        Icon = TransmitItemIconCache.GetDictIcon(),
                    };
                    RemoteItems.Add(newFolder);
                };
                aMenu.Items.Add(menu);
            }

            var curSelectedItem = MyVisualTreeHelper.GetItemOnPosition(p, e.GetPosition(p));
            if (curSelectedItem == null)
            {
                ((ListView)sender).SelectedItem = null;

                {
                    var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_upload") };
                    menu.Click += (o, a) =>
                    {
                        if (CmdUploadClipboard.CanExecute())
                            CmdUploadClipboard.Execute();
                    };
                    menu.IsEnabled = CmdUploadClipboard.CanExecute();
                    aMenu.Items.Add(menu);
                }

                {
                    var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_select_files_upload") };
                    menu.Click += (o, a) =>
                    {
                        if (CmdUpload.CanExecute())
                            CmdUpload.Execute();
                    };
                    aMenu.Items.Add(menu);
                }

                {
                    var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_select_folder_upload") };
                    menu.Click += (o, a) =>
                    {
                        if (CmdUpload.CanExecute())
                            CmdUpload.Execute(1);
                    };
                    aMenu.Items.Add(menu);
                }
            }
            else if (MyVisualTreeHelper.VisualUpwardSearch<ListViewItem>((DependencyObject)e.OriginalSource) is ListViewItem item)
            {
                {
                    var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_delete") };
                    menu.Click += (o, a) =>
                    {
                        CmdDelete.Execute();
                    };
                    aMenu.Items.Add(menu);
                }
                {
                    var menu = new System.Windows.Controls.MenuItem { Header = IoC.Translate("file_transmit_host_command_save_to") };
                    menu.Click += (o, a) =>
                    {
                        CmdDownload.Execute();
                    };
                    aMenu.Items.Add(menu);
                }
            }
            ((ListView)sender).ContextMenu = aMenu;
        }



        #region CMD

        private RelayCommand? _cmdDelete;

        public RelayCommand CmdDelete
        {
            get
            {
                return _cmdDelete ??= new RelayCommand((o) =>
                {
                    if (SelectedRemoteItem != null)
                    {
                        if (Trans?.IsConnected() != true)
                            return;

                        var vm = IoC.Get<SessionControlService>().GetTabByConnectionId(ConnectionId)?.GetViewModel();
                        if (true == MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete"), ownerViewModel: vm == null ? this : vm))
                        {
                            DeleteSelectedItems();
                        }
                    }
                });
            }
        }

        private RelayCommand? _cmdBeginRenaming;

        public RelayCommand CmdBeginRenaming
        {
            get
            {
                return _cmdBeginRenaming ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (SelectedRemoteItem != null
                        && SelectedRemoteItem.Name != "."
                        && SelectedRemoteItem.Name != ".."
                        && SelectedRemoteItem.Name != "/")
                    {
                        SelectedRemoteItem.IsRenaming = true;
                    }
                });
            }
        }

        private RelayCommand? _cmdEndRenaming;

        public RelayCommand CmdEndRenaming
        {
            get
            {
                return _cmdEndRenaming ??= new RelayCommand(async (o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (RemoteItems.Any(x => x.IsRenaming == true))
                    {
                        foreach (var item in RemoteItems.Where(x => x.IsRenaming == true))
                        {
                            var newPath = CurrentPath + "/" + item.Name;
                            if (string.IsNullOrEmpty(item.FullName) || (Trans != null && await Trans.Exists(item.FullName) == false))
                            {
                                // add
                                if (item.IsDirectory)
                                {
                                    try
                                    {
                                        await Trans.CreateDirectory(newPath);
                                        IoMessageLevel = IoMessageLevelNormal;
                                        IoMessage = $"Create folder {newPath}";
                                    }
                                    catch (Exception e)
                                    {
                                        IoMessageLevel = IoMessageLevelError;
                                        IoMessage = $"Create folder {newPath}: " + e.Message;
                                    }
                                }
                            }
                            else
                            {
                                // edit
                                try
                                {
                                    await Trans.RenameFile(item.FullName, newPath);
                                    IoMessageLevel = IoMessageLevelNormal;
                                    IoMessage = $"Move {item.FullName} => {newPath}";
                                }
                                catch (Exception e)
                                {
                                    IoMessageLevel = IoMessageLevelError;
                                    IoMessage = $"Move {item.FullName} => {newPath}: " + e.Message;
                                }
                            }
                        }

                        ShowFolder(CurrentPath, showIoMessage: false);
                    }
                });
            }
        }

        private RelayCommand? _cmdCancelRenaming;

        public RelayCommand CmdCancelRenaming
        {
            get
            {
                return _cmdCancelRenaming ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (RemoteItems.Any(x => x.IsRenaming == true))
                    {
                        var selected = "";
                        if (SelectedRemoteItem != null
                            && SelectedRemoteItem.Name != "."
                            && SelectedRemoteItem.Name != ".."
                            && SelectedRemoteItem.Name != "/")
                        {
                            selected = SelectedRemoteItem.FullName;
                        }

                        CmdGoToPathCurrent.Execute();
                        var item = RemoteItems.Where(x => x.FullName == selected);
                        if (item.Any())
                        {
                            SelectedRemoteItem = item.First();
                        }
                    }
                });
            }
        }

        private RelayCommand? _cmdListViewDoubleClick;

        /// <summary>
        /// double click to enter folder or open file
        /// </summary>
        public RelayCommand CmdListViewDoubleClick
        {
            get
            {
                return _cmdListViewDoubleClick ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (RemoteItems.Any(x => x.IsRenaming == true))
                    {
                        CmdEndRenaming.Execute();
                    }
                    else if (SelectedRemoteItem?.IsDirectory == true)
                    {
                        ShowFolder(SelectedRemoteItem.FullName);
                    }
                    else if (SelectedRemoteItem?.IsSymlink == false)
                    {
                        const int limit = 1;
                        var msg = IoC.Translate("file_transmit_host_message_preview_over_size");
                        msg = msg.Replace("1 MB", $"{limit} MB");
                        var vm = IoC.Get<SessionControlService>().GetTabByConnectionId(ConnectionId)?.GetViewModel();
                        if (SelectedRemoteItem.ByteSize > 1024 * 1024 * limit
                            && false == MessageBoxHelper.Confirm(msg, ownerViewModel: vm == null ? this : vm))
                        {
                            return;
                        }

                        try
                        {
                            var tmpPath = Path.Combine(Path.GetTempPath(), SelectedRemoteItem.Name);
                            if (File.Exists(tmpPath))
                            {
                                File.SetAttributes(tmpPath, FileAttributes.Temporary);
                                File.Delete(tmpPath);
                            }

                            var fi = new FileInfo(tmpPath);
                            var ris = RemoteItems.Where(x => x.IsSelected == true).ToArray();
                            var t = new TransmitTask(IoC.Get<ILanguageService>(), Trans, ConnectionId, fi!.Directory!.FullName, ris);
                            AddTransmitTask(t);
                            t.StartTransmitAsync(this.RemoteItems);
                            t.OnTaskEnd += (status, exception) =>
                            {
                                var item = t.Items.FirstOrDefault();
                                if (item != null)
                                {
                                    // set read only
                                    File.SetAttributes(item.DstPath, FileAttributes.ReadOnly);
                                    var psi = new System.Diagnostics.ProcessStartInfo
                                    {
                                        UseShellExecute = true,
                                        FileName = item.DstPath
                                    };
                                    System.Diagnostics.Process.Start(psi);
                                }
                            };
                        }
                        catch (Exception e)
                        {
                            IoMessageLevel = IoMessageLevelError;
                            IoMessage = e.Message;
                        }
                    }
                });
            }
        }

        private RelayCommand? _cmdGoToPathCurrent;

        public RelayCommand CmdGoToPathCurrent
        {
            get
            {
                return _cmdGoToPathCurrent ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    var selected = "";
                    if (SelectedRemoteItem != null)
                    {
                        selected = SelectedRemoteItem.FullName;
                    }

                    ShowFolder(CurrentPathEdit);
                    var item = RemoteItems.Where(x => x.FullName == selected);
                    if (!string.IsNullOrEmpty(selected) && item.Any())
                    {
                        SelectedRemoteItem = item.First();
                    }
                });
            }
        }

        private RelayCommand? _cmdGoToParent;

        public RelayCommand CmdGoToParent
        {
            get
            {
                return _cmdGoToParent ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    SimpleLogHelper.Debug($"call CmdGoToParent");
                    if (CurrentPath == "/")
                        return;
                    if (CurrentPath?.LastIndexOf("/") >= 0)
                    {
                        ShowFolder(CurrentPath.Substring(0, CurrentPath.LastIndexOf("/")));
                    }
                });
            }
        }

        private RelayCommand? _cmdGoToPathPrevious;

        public RelayCommand CmdGoToPathPrevious
        {
            get
            {
                return _cmdGoToPathPrevious ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (_pathHistoryPrevious.Count > 0)
                    {
                        SimpleLogHelper.Debug($"call CmdGoToPathPrevious");
                        var p = _pathHistoryPrevious.Pop();
                        _pathHistoryFollowing.Push(CurrentPath);
                        ShowFolder(p, 1);
                    }
                });
            }
        }

        private RelayCommand? _cmdGoToPathFollowing;
        public RelayCommand CmdGoToPathFollowing
        {
            get
            {
                return _cmdGoToPathFollowing ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (_pathHistoryFollowing.Count > 0)
                    {
                        SimpleLogHelper.Debug($"call CmdGoToPathFollowing");
                        var p = _pathHistoryFollowing.Pop();
                        _pathHistoryPrevious.Push(CurrentPath);
                        ShowFolder(p, 2);
                    }
                });
            }
        }


        private string? _lastDownloadDirPath;
        private string? LastDownloadDirPath
        {
            get
            {
                if (string.IsNullOrEmpty(_lastDownloadDirPath) || !Directory.Exists(_lastDownloadDirPath))
                    return null;
                return _lastDownloadDirPath;
            }
            set => _lastDownloadDirPath = value;
        }

        private RelayCommand? _cmdDownloadToLastDir;
        public RelayCommand CmdDownloadToLastDir
        {
            get
            {
                return _cmdDownloadToLastDir ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;

                    var selectedItems = new List<RemoteItem>();
                    if (o is RemoteItem item)
                    {
                        selectedItems.Add(item);
                    }
                    else
                    {
                        selectedItems.AddRange(RemoteItems.Where(x => x.IsSelected == true));
                    }
                    if (selectedItems.Count == 0)
                        return;

                    var destinationDirectoryPath = LastDownloadDirPath ?? "";
                    if (string.IsNullOrEmpty(destinationDirectoryPath) || !Directory.Exists(destinationDirectoryPath))
                    {
                        var path = SelectFileHelper.SaveFile(
                            title: IoC.Translate("file_transmit_host_message_files_DownloadToLastDir_to"),
                            selectedFileName: IoC.Get<ILanguageService>().Translate("file_transmit_host_message_files_download_to_dir"),
                            initialDirectory: null
                        );
                        if (path == null) return;
                        destinationDirectoryPath = new FileInfo(path).DirectoryName!;
                        if (!IoPermissionHelper.HasWritePermissionOnFile(path)
                            || !IoPermissionHelper.HasWritePermissionOnDir(destinationDirectoryPath))
                        {
                            IoMessage = IoC.Translate("string_permission_denied") + $": {path}";
                            IoMessageLevel = IoMessageLevelError;
                            return;
                        }
                    }

                    if (Directory.Exists(destinationDirectoryPath))
                    {
                        LastDownloadDirPath = destinationDirectoryPath;
                        var t = new TransmitTask(IoC.Get<ILanguageService>(), Trans, ConnectionId, destinationDirectoryPath, selectedItems.ToArray());
                        AddTransmitTask(t);
                        t.StartTransmitAsync(this.RemoteItems);
                    }
                });
            }
        }



        private RelayCommand? _cmdDownload;
        public RelayCommand CmdDownload
        {
            get
            {
                return _cmdDownload ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;

                    var selectedItems = new List<RemoteItem>();
                    if (o is RemoteItem item)
                    {
                        selectedItems.Add(item);
                    }
                    else
                    {
                        selectedItems.AddRange(RemoteItems.Where(x => x.IsSelected == true));
                    }
                    if (selectedItems.Count == 0)
                        return;

                    var path = SelectFileHelper.SaveFile(
                        title: IoC.Translate("file_transmit_host_message_files_download_to"),
                        selectedFileName: IoC.Get<ILanguageService>().Translate("file_transmit_host_message_files_download_to_dir"),
                        initialDirectory: LastDownloadDirPath
                        );
                    if (path == null) return;
                    {
                        var destinationDirectoryPath = new FileInfo(path).DirectoryName!;
                        if (!IoPermissionHelper.HasWritePermissionOnFile(path)
                            || !IoPermissionHelper.HasWritePermissionOnDir(destinationDirectoryPath))
                        {
                            IoMessage = IoC.Translate("string_permission_denied") + $": {path}";
                            IoMessageLevel = IoMessageLevelError;
                            return;
                        }

                        LastDownloadDirPath = destinationDirectoryPath;
                        var t = new TransmitTask(IoC.Get<ILanguageService>(), Trans, ConnectionId, destinationDirectoryPath, selectedItems.ToArray());
                        AddTransmitTask(t);
                        t.StartTransmitAsync(this.RemoteItems);
                    }
                });
            }
        }

        private RelayCommand? _cmdUpload;
        public RelayCommand CmdUpload
        {
            get
            {
                return _cmdUpload ??= new RelayCommand((o) =>
                {
                    if (o == null)
                    {
                        if (Trans?.IsConnected() != true)
                            return;

                        var paths = SelectFileHelper.OpenFiles(title: IoC.Get<ILanguageService>()
                            .Translate("file_transmit_host_message_select_files_to_upload"));
                        if (paths == null) return;

                        if (paths?.Length > 0)
                        {
                            DoUpload(paths.ToList());
                        }
                    }
                    else if (o is int n)
                    {
                        // upload select folder
                        if (Trans?.IsConnected() != true)
                            return;

                        var fbd = new FolderBrowserDialog();
                        fbd.ShowNewFolderButton = false;
                        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            DoUpload(new List<string>() { fbd.SelectedPath });
                        }
                    }
                });
            }
        }

        private RelayCommand? _cmdUploadClipboard;
        public RelayCommand CmdUploadClipboard
        {
            get
            {
                return _cmdUploadClipboard ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    var fl = Clipboard.GetFileDropList().Cast<string>().ToList();
                    if (fl.Count == 0)
                    {
                        return;
                    }

                    if (fl.Any(f => !File.Exists(f) && !Directory.Exists(f)))
                    {
                        return;
                    }

                    DoUpload(fl);
                }, o => Trans?.IsConnected() == true
                        && Clipboard.GetFileDropList().Count > 0
                        && Clipboard.GetFileDropList().Cast<string>().All(f => File.Exists(f) || Directory.Exists(f)));
            }
        }

        public void DoUpload(List<string> filePathList)
        {
            var fis = new List<FileInfo>();
            var dis = new List<DirectoryInfo>();
            foreach (var f in filePathList)
            {
                var fi = new FileInfo(f);
                if (fi.Exists)
                {
                    fis.Add(fi);
                }
                else
                {
                    var di = new DirectoryInfo(f);
                    if (di.Exists)
                    {
                        dis.Add(di);
                    }
                }
            }

            if (fis.Count > 0 || dis.Count > 0)
            {
                var t = new TransmitTask(IoC.Get<ILanguageService>(), Trans, ConnectionId, CurrentPath, fis.ToArray(), dis.ToArray());
                AddTransmitTask(t);
                t.StartTransmitAsync(this.RemoteItems);
            }
        }

        private RelayCommand? _cmdShowTransmitDstPath;

        public RelayCommand CmdShowTransmitDstPath
        {
            get
            {
                return _cmdShowTransmitDstPath ??= new RelayCommand((o) =>
                {
                    try
                    {
                        if (Trans?.IsConnected() != true)
                            return;
                        if (o is TransmitTask t)
                        {
                            var dst = t.TransmitDstDirectoryPath;
                            if (dst != null && string.IsNullOrEmpty(dst) == false)
                            {
                                if (t.TransmissionType == ETransmissionType.HostToServer)
                                {
                                    ShowFolder(dst);
                                }
                                else
                                {
                                    SelectFileHelper.OpenInExplorerAndSelect(dst);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                });
            }
        }

        private RelayCommand? _cmdDeleteTransmitTask;
        public RelayCommand CmdDeleteTransmitTask
        {
            get
            {
                return _cmdDeleteTransmitTask ??= new RelayCommand((o) =>
                {
                    if (Trans?.IsConnected() != true)
                        return;
                    if (o is TransmitTask t)
                    {
                        SimpleLogHelper.Debug($"Try to cancel and delete Task:{t.GetHashCode()}");
                        t.TryCancel();
                        TransmitTasks.Remove(t);
                    }
                });
            }
        }

        #endregion CMD


        #region Properties

        public ITransmitter Trans = null!;
        private readonly IFileTransmittable _protocol;

        private readonly CancellationTokenSource _consumingTransmitTaskCancellationTokenSource = new CancellationTokenSource();


        private double _gridLoadingBgOpacity = 1;
        public double GridLoadingBgOpacity
        {
            get => _gridLoadingBgOpacity;
            set => SetAndNotifyIfChanged(ref _gridLoadingBgOpacity, value);
        }

        private Visibility _gridLoadingVisibility = Visibility.Collapsed;
        public Visibility GridLoadingVisibility
        {
            get => _gridLoadingVisibility;
            set
            {
                SetAndNotifyIfChanged(ref _gridLoadingVisibility, value);
                if (GridLoadingBgOpacity > 0.99 && value != Visibility.Visible)
                {
                    GridLoadingBgOpacity = 0.1;
                    RaisePropertyChanged(nameof(GridLoadingBgOpacity));
                }
            }
        }

        private const int IoMessageLevelNormal = 0;
        private const int IoMessageLevelWarning = 1;
        private const int IoMessageLevelError = 2;
        private int _ioMessageLevel = 0;
        /// <summary>
        /// level: 0 normal; 1 warning(yellow); 2 error(red);
        /// </summary>
        public int IoMessageLevel
        {
            get => _ioMessageLevel;
            set => SetAndNotifyIfChanged(ref _ioMessageLevel, value);
        }

        private string _ioMessage = "";
        public string IoMessage
        {
            get => _ioMessage;
            set => SetAndNotifyIfChanged(ref _ioMessage, value);
        }

        public double ColumnFileNameLength
        {
            get
            {
                var w = IoC.Get<LocalityService>().FtpColumnFileNameLength;
                if (w > 50)
                {
                    return w;
                }
                return -1;
            }
            set => IoC.Get<LocalityService>().FtpColumnFileNameLength = (int)value;
        }
        public double ColumnFileTimeLength
        {
            get
            {
                var w = IoC.Get<LocalityService>().FtpColumnFileTimeLength;
                if (w > 50)
                {
                    return w;
                }
                return -1;
            }
            set => IoC.Get<LocalityService>().FtpColumnFileTimeLength = (int)value;
        }
        public double ColumnFileTypeLength
        {
            get
            {
                var w = IoC.Get<LocalityService>().FtpColumnFileTypeLength;
                if (w > 50)
                {
                    return w;
                }
                return -1;
            }
            set => IoC.Get<LocalityService>().FtpColumnFileTypeLength = (int)value;
        }
        public double ColumnFileSizeLength
        {
            get
            {
                var w = IoC.Get<LocalityService>().FtpColumnFileSizeLength;
                if (w > 50)
                {
                    return w;
                }
                return -1;
            }
            set => IoC.Get<LocalityService>().FtpColumnFileSizeLength = (int)value;
        }


        #region Path conrol
        private readonly Stack<string> _pathHistoryPrevious = new Stack<string>();
        private readonly Stack<string> _pathHistoryFollowing = new Stack<string>();


        private string _currentPathEdit = "";
        /// <summary>
        /// for ui display and edit
        /// </summary>
        public string CurrentPathEdit
        {
            get => _currentPathEdit;
            set => SetAndNotifyIfChanged(ref _currentPathEdit, value);
        }

        private string _currentPath = "";
        /// <summary>
        /// for logic control to remember current path
        /// </summary>
        private string CurrentPath
        {
            get => _currentPath;
            set
            {
                SetAndNotifyIfChanged(ref _currentPath, value);
                CurrentPathEdit = value;
            }
        }


        private bool _cmdGoToPathPreviousEnable = false;
        public bool CmdGoToPathPreviousEnable
        {
            get => _cmdGoToPathPreviousEnable;
            set => SetAndNotifyIfChanged(ref _cmdGoToPathPreviousEnable, value);
        }



        private bool _cmdGoToPathFollowingEnable = false;
        public bool CmdGoToPathFollowingEnable
        {
            get => _cmdGoToPathFollowingEnable;
            set => SetAndNotifyIfChanged(ref _cmdGoToPathFollowingEnable, value);
        }




        private bool _cmdGoToPathParentEnable = false;
        public bool CmdGoToPathParentEnable
        {
            get => _cmdGoToPathParentEnable;
            set => SetAndNotifyIfChanged(ref _cmdGoToPathParentEnable, value);
        }
        #endregion





        #region File list
        private RemoteItem? _selectedRemoteItem;
        public RemoteItem? SelectedRemoteItem
        {
            get => _selectedRemoteItem;
            set => SetAndNotifyIfChanged(ref _selectedRemoteItem, value);
        }

        private ObservableCollection<RemoteItem> _remoteItems = new ObservableCollection<RemoteItem>();
        public ObservableCollection<RemoteItem> RemoteItems
        {
            get => _remoteItems;
            set => SetAndNotifyIfChanged(ref _remoteItems, value);
        }
        #endregion




        private ObservableCollection<TransmitTask> _transmitTasks = new ObservableCollection<TransmitTask>();

        public ObservableCollection<TransmitTask> TransmitTasks
        {
            get => _transmitTasks;
            set => SetAndNotifyIfChanged(ref _transmitTasks, value);
        }
        #endregion
    }
}