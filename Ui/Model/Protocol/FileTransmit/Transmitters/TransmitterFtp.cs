using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;

namespace _1RM.Model.Protocol.FileTransmit.Transmitters
{
    public class TransmitterFtp : ITransmitter
    {
        public readonly string Hostname;
        public readonly int Port;
        public readonly string Username;
        public readonly string Password;
        private Task FtpConnection;
        private SemaphoreSlim FtpSemaphoe;
        private AsyncFtpClient? _ftp = null;

        public TransmitterFtp(string host, int port, string username, string password)
        {
            Hostname = host;
            Port = port;
            Username = username;
            Password = password;
            FtpSemaphoe = new SemaphoreSlim(1, 1);
            FtpConnection = InitClient();
        }

        ~TransmitterFtp()
        {
            Release();
        }
        public async Task Conn()
        {
            await FtpConnection;
        }

        public bool IsConnected()
        {
            return _ftp?.IsConnected == true;
        }

        public ITransmitter Clone()
        {
            return new TransmitterFtp(Hostname, Port, Username, Password);
        }

        public async Task<RemoteItem?> Get(string path)
        {
            await FtpConnection;
            if (_ftp == null) return null;
            return await Exists(path) ? FtpListItem2RemoteItem(await _ftp.GetObjectInfo(path)) : null;
        }

        public async Task<List<RemoteItem>> ListDirectoryItems(string path)
        {
            await FtpConnection;
            var ret = new List<RemoteItem>();
            if (_ftp != null)
            {
                IEnumerable<FtpListItem> items = await _ftp.GetListing(path);
                if (items == null ||
                    !items.Any())
                    return ret;

                items = items.OrderBy(x => x.Name);
                foreach (var item in items)
                {
                    if (item.Name == "." || item.Name == "..")
                        continue;
                    ret.Add(FtpListItem2RemoteItem(item));
                }
            }
            return ret;
        }

        public async Task<bool> Exists(string path)
        {
            await FtpConnection;
            if (_ftp == null) return false;
            if (await _ftp.FileExists(path))
                return true;
            if (await _ftp.DirectoryExists(path))
                return true;
            return false;
        }

        private RemoteItem FtpListItem2RemoteItem(FtpListItem item)
        {
            var fn = item.FullName;
            var newItem = new RemoteItem()
            {
                Icon = null,
                IsDirectory = item.Type == FtpObjectType.Directory,
                Name = item.Name,
                FullName = fn,
                LastUpdate = item.RawModified,
                ByteSize = (ulong)Math.Max(item.Size, 0),
                IsSymlink = item.Type == FtpObjectType.Link,
            };
            if (item.Type == FtpObjectType.Directory)
            {
                if (newItem.IsSymlink)
                {
                    newItem.Icon = TransmitItemIconCache.GetDictIcon(Environment.GetFolderPath(Environment.SpecialFolder.Favorites));
                }
                else
                {
                    newItem.Icon = TransmitItemIconCache.GetDictIcon();
                }
                newItem.ByteSize = 0;
                newItem.FileType = "folder";
            }
            else
            {
                if (item.Name.IndexOf(".", StringComparison.Ordinal) > 0)
                {
                    var ext = item.Name.Substring(item.Name.LastIndexOf(".", StringComparison.Ordinal)).ToLower();
                    newItem.FileType = ext;
                    newItem.Icon = TransmitItemIconCache.GetFileIcon(ext);
                }
                else
                {
                    newItem.Icon = TransmitItemIconCache.GetFileIcon();
                }
            }
            return newItem;
        }

        public async Task Delete(string path)
        {
            await FtpConnection;
            if (_ftp == null) return;
            var item = await Get(path);
            if (item != null)
            {
                if (item.IsDirectory)
                {
                    await _ftp.DeleteDirectory(path);
                }
                else
                {
                    await _ftp.DeleteFile(path);
                }
            }
        }

        public async Task Delete(RemoteItem item)
        {
            await Delete(item.FullName);
        }

        public async Task CreateDirectory(string path)
        {
            await FtpConnection;
            if (_ftp == null) return;
            if (await _ftp.DirectoryExists(path) == false)
                await _ftp.CreateDirectory(path);
        }

        public async Task RenameFile(string path, string newPath)
        {
            await FtpConnection;
            if (_ftp != null && path != newPath && await Exists(path))
                await _ftp.Rename(path, newPath);
        }

        public async Task UploadFile(string localFilePath, string saveToRemotePath, Action<ulong> writeCallBack, CancellationToken cancellationToken)
        {
            var fi = new FileInfo(localFilePath);
            if (fi?.Exists != true)
                return;

            await FtpConnection;
            if (_ftp == null) return;
            await FtpSemaphoe.WaitAsync().ConfigureAwait(false);
            try
            {
                // check parent
                if (saveToRemotePath.LastIndexOf("/", StringComparison.Ordinal) > 0)
                {
                    var parent = saveToRemotePath.Substring(0,
                        saveToRemotePath.LastIndexOf("/", StringComparison.Ordinal));
                    if (await _ftp.DirectoryExists(parent) == false)
                        await _ftp.CreateDirectory(parent);
                }

                await _ftp.UploadFile(localFilePath, saveToRemotePath, FtpRemoteExists.Overwrite, true, FtpVerify.Delete,
                    new Progress<FtpProgress>(progress =>
                    {
                        writeCallBack?.Invoke((ulong)progress.TransferredBytes);
                    }), cancellationToken);
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested == false)
                    throw;
            }
            finally
            {
                FtpSemaphoe.Release();
            }
        }

        public async Task DownloadFile(string remoteFilePath, string saveToLocalPath, Action<ulong> readCallBack, CancellationToken cancellationToken)
        {
            await FtpConnection;
            if (_ftp == null) return;
            await FtpSemaphoe.WaitAsync().ConfigureAwait(false);
            try
            {
                await _ftp.DownloadFile(saveToLocalPath, remoteFilePath, FtpLocalExists.Overwrite, FtpVerify.None,
                    new Progress<FtpProgress>(progress => readCallBack?.Invoke((ulong)progress.TransferredBytes)),
                    cancellationToken);
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested == false)
                    throw;
            }
            finally
            {
                FtpSemaphoe.Release();
            }
        }

        public void Release()
        {
            FtpSemaphoe?.Dispose();
            FtpConnection?.Dispose();
            var ftp = _ftp;
            ftp?.Disconnect();
            ftp?.Dispose();
            _ftp = null;
        }

        private async Task InitClient()
        {
            _ftp?.Dispose();
            _ftp = new AsyncFtpClient(Hostname, new System.Net.NetworkCredential(Username, Password), Port);
            _ftp.Config.Noop = true;
            await _ftp.AutoConnect();
        }
    }
}