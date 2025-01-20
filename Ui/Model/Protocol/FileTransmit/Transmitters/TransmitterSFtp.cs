using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using _1RM.Utils;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Shawn.Utils;

namespace _1RM.Model.Protocol.FileTransmit.Transmitters
{
    public class TransmitterSFtp : ITransmitter
    {
        public readonly string Hostname;
        public readonly int Port;
        public readonly string Username;
        public readonly string Password;
        public readonly string SshKeyPath;
        private Task SFtpConnection;
        private SftpClient? _sftp = null;

        public TransmitterSFtp(string host, int port, string username, string key, bool keyIsPassword)
        {
            Hostname = host;
            Port = port;
            Username = username;
            if (keyIsPassword)
            {
                Password = key;
                SshKeyPath = "";
            }
            else
            {
                Password = "";
                SshKeyPath = key;
            }
            SFtpConnection = InitClient();
        }

        ~TransmitterSFtp()
        {
            Release();
        }

        public async Task Conn()
        {
            await SFtpConnection;
        }

        public bool IsConnected()
        {
            return _sftp?.IsConnected == true;
        }

        public ITransmitter Clone()
        {
            if (!string.IsNullOrWhiteSpace(Password))
                return new TransmitterSFtp(Hostname, Port, Username, Password, true);
            else
                return new TransmitterSFtp(Hostname, Port, Username, SshKeyPath, false);
        }

        public async Task<RemoteItem?> Get(string path)
        {
            await SFtpConnection;
            if (_sftp == null) return null;
            return await Exists(path) ? SftpFile2RemoteItem(_sftp.Get(path)) : null;
        }

        public async Task<List<RemoteItem>> ListDirectoryItems(string path)
        {
            await SFtpConnection;
            var ret = new List<RemoteItem>();
            if (_sftp != null)
            {
                IEnumerable<ISftpFile> items = new List<SftpFile>();
                items = _sftp.ListDirectory(path);
                if (items == null || !items.Any())
                    return ret;

                items = items.OrderBy(x => x.Name);
                foreach (var item in items)
                {
                    if (item.Name == "." || item.Name == "..")
                        continue;
                    ret.Add(SftpFile2RemoteItem(item));
                }
            }
            return ret;
        }

        public async Task<bool> Exists(string path)
        {
            await SFtpConnection;
            return _sftp?.Exists(path) == true;
        }

        private RemoteItem SftpFile2RemoteItem(ISftpFile item)
        {
            var fn = item.FullName;
            var newItem = new RemoteItem()
            {
                Icon = null,
                IsDirectory = item.IsDirectory,
                Name = item.Name,
                FullName = fn,
                LastUpdate = item.LastWriteTime,
                ByteSize = (ulong)item.Length,
            };
            if (item.IsDirectory)
            {
                newItem.Icon = TransmitItemIconCache.GetDictIcon();
                newItem.ByteSize = 0;
                newItem.FileType = "folder";
                if (item.IsSymbolicLink)
                    newItem.Icon = TransmitItemIconCache.GetDictIcon(Environment.GetFolderPath(Environment.SpecialFolder.Favorites));
            }
            else
            {
                if (item.IsSymbolicLink)
                    newItem.FileType = ".lnk";

                BitmapSource icon;
                if (item.Name.IndexOf(".", StringComparison.Ordinal) > 0)
                {
                    var ext = item.Name.Substring(item.Name.LastIndexOf(".", StringComparison.Ordinal)).ToLower();
                    newItem.FileType = ext;
                    icon = TransmitItemIconCache.GetFileIcon(ext);
                }
                else
                {
                    icon = TransmitItemIconCache.GetFileIcon();
                }
                newItem.Icon = icon;
            }
            return newItem;
        }

        public async Task Delete(string path)
        {
            await SFtpConnection;
            if (_sftp == null) return;
            var item = await Get(path);
            if (item != null)
            {
                if (item.IsDirectory)
                {
                    var sub = _sftp.ListDirectory(path) ?? new List<SftpFile>();
                    foreach (var file in sub)
                    {
                        if (string.IsNullOrWhiteSpace(
                                file.Name
                                    .Replace('.', ' ')
                                    .Replace('\\', ' ')
                                    .Replace('/', ' ')))
                            continue;
                        await Delete((string)file.FullName);
                    }

                    _sftp.DeleteDirectory(path);
                }
                else
                    _sftp.Delete(path);
            }
        }

        public async Task Delete(RemoteItem item)
        {
            await Delete(item.FullName);
        }

        public async Task CreateDirectory(string path)
        {
            await SFtpConnection;
            if (_sftp == null) return;
            if (_sftp.Exists(path) == false)
                _sftp.CreateDirectory(path);
        }

        public async Task RenameFile(string path, string newPath)
        {
            await SFtpConnection;
            if (_sftp == null) return;
            if (_sftp != null && path != newPath && await Exists(path) == true)
                _sftp.RenameFile(path, newPath);
        }

        public async Task UploadFile(string localFilePath, string saveToRemotePath, Action<ulong> writeCallBack, CancellationToken cancellationToken)
        {
            var fi = new FileInfo(localFilePath);
            if (fi?.Exists != true)
                return;

            await SFtpConnection;
            if (_sftp == null) return;
            try
            {
                // check parent
                if (saveToRemotePath.LastIndexOf("/", StringComparison.Ordinal) > 0)
                {
                    var parent = saveToRemotePath.Substring(0,
                        saveToRemotePath.LastIndexOf("/", StringComparison.Ordinal));
                    if (_sftp.Exists(parent) == false)
                        _sftp.CreateDirectory(parent);
                }

                using var fileStream = File.OpenRead(fi.FullName);
                if (!fileStream.CanRead)
                    return;

                _sftp.UploadFile(fileStream, saveToRemotePath, obj =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SimpleLogHelper.Debug("SFTP Upload: cancel by CancellationToken");
                        fileStream.Close();
                        fileStream.Dispose();
                    }
                    writeCallBack?.Invoke(obj);
                });
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested == false)
                    throw;
            }
        }

        public async Task DownloadFile(string remoteFilePath, string saveToLocalPath, Action<ulong> readCallBack, CancellationToken cancellationToken)
        {
            await SFtpConnection;
            if (_sftp == null) return;
            try
            {
                var fi = new FileInfo(saveToLocalPath);
                if (fi.Exists)
                    fi.Delete();
                if (fi?.Directory?.Exists == false)
                    fi.Directory.Create();
                using var fileStream = File.OpenWrite(saveToLocalPath);
                if (!fileStream.CanWrite)
                    return;

                _sftp.DownloadFile(remoteFilePath, fileStream, obj =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        fileStream.Close();
                    readCallBack?.Invoke(obj);
                });
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested == false)
                    throw;
            }
        }

        public void Release()
        {
            SFtpConnection?.Dispose();
            var sftp = _sftp;
            sftp?.Disconnect();
            sftp?.Dispose();
            _sftp = null;
        }

        private async Task InitClient()
        {
            await Task.Run(() =>
            {
                if (_sftp?.IsConnected != true)
                {
                    RetryHelper.Try(() =>
                    {
                        _sftp?.Dispose();
                        if (string.IsNullOrEmpty(Password)
                            && string.IsNullOrEmpty(SshKeyPath) == false
                            && File.Exists(SshKeyPath))
                        {
                            try
                            {
                                var connectionInfo = new ConnectionInfo(Hostname, Port, Username, new PrivateKeyAuthenticationMethod(Username, new PrivateKeyFile(SshKeyPath)));
                                _sftp = new SftpClient(connectionInfo);
                            }
                            catch (Exception e)
                            {
                                MsAppCenterHelper.Error(e);
                            }
                        }
                        _sftp ??= new SftpClient(new ConnectionInfo(Hostname, Port, Username, new PasswordAuthenticationMethod(Username, Password)));
                        //_sftp.KeepAliveInterval = new TimeSpan(0, 0, 10);
                        _sftp.Connect();
                    });
                }
            });
        }
    }
}