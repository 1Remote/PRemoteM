using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Shawn.Utils;
using Shawn.Utils.Wpf.Image;

namespace _1RM.Model.Protocol.FileTransmit.Transmitters
{
    public static class TransmitItemIconCache
    {
        private static readonly Dictionary<string, BitmapSource> _fileIcons = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, BitmapSource> _dictIcons = new Dictionary<string, BitmapSource>();
        private static readonly object _locker = new object();

        public static BitmapSource GetFileIcon(string key = ".tmz")
        {
            lock (_locker)
            {
                if (_fileIcons.ContainsKey(".tmz") == false)
                    _fileIcons.Add(".tmz", SystemIconHelper.GetIcon(".tmz")!);
                if (_fileIcons.ContainsKey(key))
                    return _fileIcons[key];
                var icon = SystemIconHelper.GetIcon(key, isFile: true);
                _fileIcons.Add(key, icon ?? _fileIcons[".tmz"]);
                return _fileIcons[key];
            }
        }

        public static BitmapSource GetDictIcon(string key = "")
        {
            lock (_locker)
            {
                if (_dictIcons.ContainsKey("") == false)
                {
                    _dictIcons.Add("", SystemIconHelper.GetIcon("", isDir: true)!);
                }
                if (_dictIcons.ContainsKey(key))
                    return _dictIcons[key];
                var icon = SystemIconHelper.GetIcon(key, isDir: true);
                _dictIcons.Add(key, icon ?? _dictIcons[""]);
                return _dictIcons[key];
            }
        }
    }

    public interface ITransmitter
    {
        Task Conn();

        bool IsConnected();

        ITransmitter Clone();

        Task<RemoteItem?> Get(string path);

        /// <summary>
        /// get items of a directory, sub-directory treat as a item too
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        Task<List<RemoteItem>> ListDirectoryItems(string path);

        Task<bool> Exists(string path);

        Task Delete(string path);

        Task Delete(RemoteItem item);

        Task CreateDirectory(string path);

        Task RenameFile(string path, string newPath);

        /// <summary>
        /// write local file to server path
        /// </summary>
        /// <param name="localFilePath"></param>
        /// <param name="saveToRemotePath"></param>
        /// <param name="writeCallBack">callback will offer data length has been written</param>
        /// <param name="cancellationToken"></param>
        Task UploadFile(string localFilePath, string saveToRemotePath, Action<ulong> writeCallBack, CancellationToken cancellationToken);

        /// <summary>
        /// read server path and written to local fileStream\
        /// </summary>
        /// <param name="remoteFilePath"></param>
        /// <param name="saveToLocalPath"></param>
        /// <param name="readCallBack">callback will offer data length has been written</param>
        /// <param name="cancellationToken"></param>
        Task DownloadFile(string remoteFilePath, string saveToLocalPath, Action<ulong> readCallBack, CancellationToken cancellationToken);

        void Release();
    }
}