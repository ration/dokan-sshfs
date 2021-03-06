using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Text;

using DokanNet;
using Tamir.SharpSsh.jsch;
using System.Security.AccessControl;

namespace DokanSSHFS
{
    class GetMonitor : SftpProgressMonitor
    {
        private long offset_;
        private long maxOffset_;

        public GetMonitor(long max)
        {
            maxOffset_ = max;
        }

        public override bool count(long count)
        {
            offset_ += count;
            Console.Error.WriteLine("count = {0}, offset = {1}", count, offset_);

            return offset_ < maxOffset_;
        }

        public override void end()
        {
            Console.Error.WriteLine("end");
        }

        public override void init(int op, string src, string dest, long max)
        {
            Console.Error.WriteLine("init file: {0} size: {1}", src, max);
        }
    }

    class GetStream : Tamir.SharpSsh.java.io.OutputStream
    {
        private byte[] buffer_;
        private int index_;

        public int RecievedBytes
        {
            get
            {
                return index_;
            }
        }

        public GetStream(byte[] buffer)
        {
            buffer_ = buffer;
            index_ = 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            //Console.Error.WriteLine("Write index: {0} count {1}", index_, count);
            // full of buffer
            if (isFull())
                return;

            int rest = buffer_.Length - index_;
            count = rest < count ? rest : count;
            Array.Copy(buffer, offset, buffer_, index_, count);
            index_ += count;
        }

        public bool isFull()
        {
            return buffer_.Length <= index_;
        }
    }

    class SSHFS : IDokanOperations
    {
        private JSch jsch_;
        private Session session_;

        private object sessionLock_ = new Object();
        private Dictionary<int, ChannelSftp> channels_;

        private string user_;
        private string host_;
        private int port_;
        private string identity_;
        private bool debug_;
        private string root_;
        private string passphrase_;
        private string password_;

        private System.IO.TextWriter tw_;

        private int trycount_ = 0;
        private bool connectionError_ = false;
        private object reconnectLock_ = new object();

        public SSHFS()
        {
        }

        public void Initialize(string user, string host, int port, string password, string identity, string passphrase, string root, bool debug)
        {
            user_ = user;
            host_ = host;
            port_ = port;
            identity_ = identity;
            password_ = password;
            passphrase_ = passphrase;

            root_ = root;

            debug_ = debug;

            if (debug_ && tw_ != null)
            {
                System.IO.StreamWriter sw = new System.IO.StreamWriter(Application.UserAppDataPath + "\\error.txt");
                sw.AutoFlush = true;
                tw_ = System.IO.TextWriter.Synchronized(sw);
                Console.SetError(tw_);
            }

        }

        private void Debug(string format, params object[] args)
        {
            if (debug_)
            {
                Console.Error.WriteLine("SSHFS: " + format, args);
                System.Diagnostics.Debug.WriteLine(string.Format("SSHFS: " + format, args));
            }
        }

        internal bool SSHConnect()
        {
            try
            {
                channels_ = new Dictionary<int, ChannelSftp>();

                jsch_ = new JSch();
                Hashtable config = new Hashtable();
                config["StrictHostKeyChecking"] = "no";

                if (identity_ != null)
                    jsch_.addIdentity(identity_, passphrase_);

                session_ = jsch_.getSession(user_, host_, port_);
                session_.setConfig(config);
                session_.setUserInfo(new DokanUserInfo(password_, passphrase_));
                session_.setPassword(password_);

                session_.connect();

                return true;
            }
            catch (Exception e)
            {
                Debug(e.ToString());
                return false;
            }
        }

        private bool Reconnect()
        {
            lock (reconnectLock_)
            {
                if (!connectionError_)
                    return true;

                Debug("Disconnect current sessions\n");
                try
                {
                    foreach (KeyValuePair<int, ChannelSftp> kv in channels_)
                    {
                        kv.Value.disconnect();
                    }

                    session_.disconnect();
                }
                catch (Exception e)
                {
                    Debug(e.ToString());
                }

                Debug("Reconnect {0}\n", trycount_);

                trycount_++;

                if (SSHConnect())
                {
                    Debug("Reconnect success\n");
                    connectionError_ = false;
                    return true;
                }
                else
                {
                    Debug("Reconnect failed\n");
                    return false;
                }
            }
        }

        private string GetPath(string filename)
        {
            string path = root_ + filename.Replace('\\', '/');
            Debug("GetPath : {0} thread {1}", path, System.Threading.Thread.CurrentThread.ManagedThreadId);
            //Debug("  Stack {0}", new System.Diagnostics.StackTrace().ToString());
            return path;
        }


        private ChannelSftp GetChannel()
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;
            ChannelSftp channel;
            try
            {
                channel = channels_[threadId];
            }
            catch(KeyNotFoundException)
            {

                lock (sessionLock_)
                {
                    channel = (ChannelSftp)session_.openChannel("sftp");
                    channel.connect();
                    channels_[threadId] = channel;
                }
            }
            return channel;
        }

        private bool isExist(string path, DokanFileInfo info)
        {
            try
            {
                ChannelSftp channel = GetChannel();
                SftpATTRS attr = channel.stat(path);
                if (attr.isDir())
                    info.IsDirectory = true;
                return true;
            }
            catch (SftpException)
            {
                return false;
            }
        }


        private string ReadPermission(string path)
        {
            try
            {
                SftpATTRS attr = GetChannel().stat(path);
                return Convert.ToString(attr.getPermissions() & 0xFFF, 8) + "\n";
            }
            catch (SftpException)
            {
                return "";
            }
            catch (Exception)
            {
                connectionError_ = true;
                Reconnect();
                return "";
            }
        }


        private bool CheckAltStream(string filename)
        {
            if (filename.Contains(":"))
            {
                string[] tmp = filename.Split(new char[] { ':' }, 2);

                if (tmp.Length != 2)
                    return false;

                if (tmp[1].StartsWith("SSHFSProperty."))
                    return true;

                return false;
            }
            else
            {
                return false;
            }
        }


        public DokanError CreateFile(
            string filename,
            DokanNet.FileAccess access,
            FileShare share,
            FileMode mode,
            FileOptions options,
            FileAttributes attributes,
            DokanFileInfo info)
        {

            Debug("CreateFile {0}", filename);
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();

                if (CheckAltStream(path))
                    return DokanError.ErrorSuccess;

                switch (mode)
                {
                    case FileMode.Open:
                        {
                            Debug("Open");
                            if (isExist(path, info))
                                return DokanError.ErrorSuccess;
                            else
                                return DokanError.ErrorFileNotFound;
                        }
                    case FileMode.CreateNew:
                        {
                            Debug("CreateNew");
                            if (isExist(path, info))
                                return DokanError.ErrorAlreadyExists;

                            Debug("CreateNew put 0 byte");
                            Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path);
                            stream.Close();
                            return DokanError.ErrorSuccess;
                        }
                    case FileMode.Create:
                        {
                            Debug("Create put 0 byte");
                            Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path);
                            stream.Close();
                            return DokanError.ErrorSuccess;
                        }
                    case FileMode.OpenOrCreate:
                        {
                            Debug("OpenOrCreate");

                            if (!isExist(path, info))
                            {
                                Debug("OpenOrCreate put 0 byte");
                                Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path);
                                stream.Close();
                            }
                            return DokanError.ErrorSuccess;
                        }
                    case FileMode.Truncate:
                        {
                            Debug("Truncate");

                            if (!isExist(path, info))
                                return DokanError.ErrorFileNotFound;

                            Debug("Truncate put 0 byte");
                            Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path);
                            stream.Close();
                            return DokanError.ErrorSuccess;
                        }
                    case FileMode.Append:
                        {
                            Debug("Append");

                            if (isExist(path, info))
                                return DokanError.ErrorSuccess;

                            Debug("Append put 0 byte");
                            Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path);
                            stream.Close();
                            return DokanError.ErrorSuccess;
                        }
                    default:
                        Debug("Error unknown FileMode {0}", mode);
                        return DokanError.ErrorError;
                }

            }
            catch (SftpException e)
            {
                Debug(e.ToString());
                return DokanError.ErrorFileNotFound;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorFileNotFound;
            }
        }

        public DokanError OpenDirectory(
            string filename,
            DokanFileInfo info)
        {
            Debug("OpenDirectory {0}", filename);
            try
            {
                string path = GetPath(filename);
                SftpATTRS attr = GetChannel().stat(path);
                if (attr.isDir())
                {
                    return DokanError.ErrorSuccess;
                }
                else
                {
                    return DokanError.ErrorPathNotFound; // TODO: return not directory?
                }
            }
            catch (SftpException e)
            {
                Debug(e.ToString());
                return DokanError.ErrorPathNotFound;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorPathNotFound;
            }
        }

        public DokanError CreateDirectory(
            string filename,
            DokanFileInfo info)
        {
            Debug("CreateDirectory {0}", filename);
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();

                channel.mkdir(path);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException e)
            {
                Debug(e.ToString());
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError; // TODO: more appropriate error code
            }
        }

        public DokanError Cleanup(
            string filename,
            DokanFileInfo info)
        {
            return DokanError.ErrorSuccess;
        }

        public DokanError CloseFile(
            string filename,
            DokanFileInfo info)
        {
            return DokanError.ErrorSuccess;
        }


        public DokanError ReadFile(
            string filename,
            Byte[] buffer,
            out int readBytes,
            long offset,
            DokanFileInfo info)
        {
            string path = GetPath(filename);

            readBytes = 0;
            if (path.Contains(":SSHFSProperty.Permission"))
            {
                if (offset == 0)
                {
                    string[] tmp = path.Split(new char[] { ':' });
                    path = tmp[0];
                    string str = ReadPermission(path);
                    byte[] bytes = System.Text.Encoding.ASCII.GetBytes(str);
                    int min = (buffer.Length < bytes.Length ? buffer.Length : bytes.Length);
                    Array.Copy(bytes, buffer, min);
                    readBytes = min;
                    return DokanError.ErrorSuccess;
                }
                else
                {
                    return DokanError.ErrorSuccess;
                }
            }

            if (info.IsDirectory)
                return DokanError.ErrorError;


            Debug("ReadFile {0} bufferLen {1} Offset {2}", filename, buffer.Length, offset);
            try
            {
                ChannelSftp channel = GetChannel();
                GetMonitor monitor = new GetMonitor(offset + buffer.Length);
                GetStream stream = new GetStream(buffer);
                channel.get(path, stream, monitor, ChannelSftp.RESUME, offset);
                readBytes = stream.RecievedBytes;
                Debug("  ReadFile readBytes: {0}", readBytes);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }


        class PutMonitor : SftpProgressMonitor
        {
            private long offset_;
            public PutMonitor()
            {
                offset_ = 0;
            }

            public override bool count(long count)
            {
                offset_ += count;
                //Console.Error.WriteLine("count = {0}, offset = {1}", count, offset_);
                return true;
            }

            public override void end()
            {
                //Console.Error.WriteLine("end");
            }

            public override void init(int op, string src, string dest, long max)
            {
                //Console.Error.WriteLine("init file: {0} size: {1}", src, max);
            }
        }


        private bool WritePermission(
            string path,
            int permission)
        {
            try
            {
                Debug("WritePermission {0}:{1}", path, Convert.ToString(permission, 8));
                ChannelSftp channel = GetChannel();
                SftpATTRS attr = channel.stat(path);
                attr.setPERMISSIONS(permission);
                channel.setStat(path, attr);
            }
            catch (SftpException)
            {
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
            }
            return true;
        }

        public DokanError WriteFile(
            string filename,
            byte[] buffer,
            out int writtenBytes,
            long offset,
            DokanFileInfo info)
        {
            Debug("WriteFile {0} bufferLen {1} Offset {2}", filename, buffer.Length, offset);
            
            string path = GetPath(filename);

            writtenBytes = 0;
            if (path.Contains(":SSHFSProperty.Permission"))
            {
                if (offset == 0)
                {
                    string[] tmp = path.Split(new char[] { ':' });
                    path = tmp[0];
                    int permission = 0;
                    permission = Convert.ToInt32(System.Text.Encoding.ASCII.GetString(buffer), 8);
                    WritePermission(path, permission);
                    writtenBytes = buffer.Length;
                    return DokanError.ErrorSuccess;
                }
                else
                {
                    return DokanError.ErrorSuccess;
                }
            }

            try
            {
                ChannelSftp channel = GetChannel();
                //GetMonitor monitor = new GetMonitor(buffer.Length);
                Tamir.SharpSsh.java.io.OutputStream stream = channel.put(path, null, 3/*HACK: 存在しないモード */, offset);
                stream.Write(buffer, 0, buffer.Length);
                stream.Close();
                writtenBytes = buffer.Length;
                return DokanError.ErrorSuccess;
            }
            catch (IOException)
            {
                return DokanError.ErrorSuccess;
            }
            catch (Exception e)
            {
                Debug(e.ToString());
                return DokanError.ErrorError;
            }
        }

        public DokanError FlushFileBuffers(
            string filename,
            DokanFileInfo info)
        {
            return DokanError.ErrorSuccess;
        }

        public DokanError GetFileInformation(
            string filename,
            out FileInformation fileinfo,
            DokanFileInfo info)
        {
            fileinfo = new FileInformation();
            try
            {
                string path = GetPath(filename);
                SftpATTRS attr = GetChannel().stat(path);

                fileinfo.Attributes = attr.isDir() ?
                    FileAttributes.Directory :
                    FileAttributes.Normal;

                if (DokanSSHFS.UseOffline)
                    fileinfo.Attributes |= FileAttributes.Offline;

                DateTime org = new DateTime(1970, 1, 1, 0, 0, 0, 0);

                fileinfo.CreationTime = org.AddSeconds(attr.getMTime());
                fileinfo.LastAccessTime = org.AddSeconds(attr.getATime());
                fileinfo.LastWriteTime = org.AddSeconds(attr.getMTime());
                fileinfo.Length = attr.getSize();

                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError FindFiles(
            string filename,
            out IList<FileInformation> files,
            DokanFileInfo info)
        {
            Debug("FindFiles {0}", filename);

            files = new List<FileInformation>();
            try
            {
                string path = GetPath(filename);
                ArrayList entries = (ArrayList)GetChannel().ls(path);

                foreach (ChannelSftp.LsEntry entry in entries)
                {

                    FileInformation fi = new FileInformation();

                    fi.Attributes = entry.getAttrs().isDir() ?
                        FileAttributes.Directory :
                        FileAttributes.Normal;

                    if (DokanSSHFS.UseOffline)
                        fi.Attributes |= FileAttributes.Offline;

                    DateTime org = new DateTime(1970, 1, 1, 0, 0, 0, 0);

                    fi.CreationTime = org.AddSeconds(entry.getAttrs().getMTime());
                    fi.LastAccessTime = org.AddSeconds(entry.getAttrs().getATime());
                    fi.LastWriteTime = org.AddSeconds(entry.getAttrs().getMTime());
                    fi.Length = entry.getAttrs().getSize();
                    //fi.FileName = System.Text.Encoding.UTF8.GetString(entry.getFilename().getBytes());
                    fi.FileName = entry.getFilename();

                    if (fi.FileName.StartsWith("."))
                    {
                        fi.Attributes |= FileAttributes.Hidden;
                    }
                    files.Add(fi);
                }
                return DokanError.ErrorSuccess;

            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError SetFileAttributes(
            string filename,
            FileAttributes attr,
            DokanFileInfo info)
        {
            Debug("SetFileAttributes {0}", filename);
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();
                SftpATTRS sattr = channel.stat(path);

                int permissions = sattr.getPermissions();
                Debug(" permissons {0} {1}", permissions, sattr.getPermissionsString());
                sattr.setPERMISSIONS(permissions);
                channel.setStat(path, sattr);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError SetFileTime(
            string filename,
            DateTime? ctime,
            DateTime? atime,
            DateTime? mtime,
            DokanFileInfo info)
        {
            Debug("SetFileTime {0}", filename);
            try
            {
                Debug(" filetime {0} {1} {2}", ctime.HasValue ? ctime.ToString() : "-", atime.HasValue ? atime.ToString() : "-", mtime.HasValue ? mtime.ToString() : "-");

                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();
                SftpATTRS attr = channel.stat(path);

                TimeSpan at = (atime.GetValueOrDefault(DateTime.MinValue) - new DateTime(1970, 1, 1, 0, 0, 0));
                TimeSpan mt = (mtime.GetValueOrDefault(DateTime.MinValue) - new DateTime(1970, 1, 1, 0, 0, 0));

                int uat = (int)at.TotalSeconds;
                int umt = (int)mt.TotalSeconds;

                if (mtime == DateTime.MinValue)
                    umt = attr.getMTime();
                if (atime == DateTime.MinValue)
                    uat = attr.getATime();

                attr.setACMODTIME(uat, umt);
                channel.setStat(path, attr);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError DeleteFile(
            string filename,
            DokanFileInfo info)
        {
            Debug("DeleteFile {0}", filename);
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();
                channel.rm(path);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError DeleteDirectory(
            string filename,
            DokanFileInfo info)
        {
            Debug("DeleteDirectory {0}", filename);
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();
                channel.rmdir(path);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError MoveFile(
            string filename,
            string newname,
            bool replace,
            DokanFileInfo info)
        {
            Debug("MoveFile {0}", filename);
            try
            {
                string oldPath = GetPath(filename);
                string newPath = GetPath(newname);
                ChannelSftp channel = GetChannel();
                channel.rename(oldPath, newPath);
                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError SetEndOfFile(
            string filename,
            long length,
            DokanFileInfo info)
        {
            try
            {
                string path = GetPath(filename);             
                ChannelSftp channel = GetChannel();
                SftpATTRS attr = channel.stat(path);

                attr.setSIZE(length);
                channel.setStat(path, attr);

                return DokanError.ErrorSuccess;
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
        }

        public DokanError SetAllocationSize(string filename, long length, DokanFileInfo info)
        {
            try
            {
                string path = GetPath(filename);
                ChannelSftp channel = GetChannel();
                SftpATTRS attr = channel.stat(path);
                if (attr.getSize() < length)
                {
                    attr.setSIZE(length);
                }
                channel.setStat(path, attr);
            }
            catch (SftpException)
            {
                return DokanError.ErrorError;
            }
            catch (Exception e)
            {
                connectionError_ = true;
                Debug(e.ToString());
                Reconnect();
                return DokanError.ErrorError;
            }
            return DokanError.ErrorSuccess;
        }

        public DokanError LockFile(
            string filename,
            long offset,
            long length,
            DokanFileInfo info)
        {
            return DokanError.ErrorSuccess;
        }

        public DokanError UnlockFile(
            string filename,
            long offset,
            long length,
            DokanFileInfo info)
        {
            return DokanError.ErrorSuccess;
        }

        public DokanError GetDiskFreeSpace(
                     out long freeBytesAvailable,
                     out long totalBytes,
                     out long totalFreeBytes,
                     DokanFileInfo info)
        {
            freeBytesAvailable = 1024L * 1024 * 1024 * 10;
            totalBytes = 1024L * 1024 * 1024 * 20;
            totalFreeBytes = 1024L * 1024 * 1024 * 10;
            return DokanError.ErrorSuccess;
        }

        public DokanError Unmount(
            DokanFileInfo info)
        {
            try
            {
                Debug("disconnection...");

                GetChannel().exit();

                Thread.Sleep(1000 * 1);

                foreach (KeyValuePair<int, ChannelSftp> kv in channels_)
                {
                    kv.Value.disconnect();
                }

                session_.disconnect();

                Debug("disconnected");
            }
            catch (Exception e)
            {
                Debug(e.ToString());
            }
            return DokanError.ErrorSuccess;
        }

        public DokanError GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)
        {
            security = null;
            return DokanError.ErrorError;
        }

        public DokanError SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)
        {
            return DokanError.ErrorError;
        }

        public DokanError GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, DokanFileInfo info)
        {
            volumeLabel = String.Empty;
            features = FileSystemFeatures.None;
            fileSystemName = String.Empty;
            return DokanError.ErrorError;
        }
    }
}
