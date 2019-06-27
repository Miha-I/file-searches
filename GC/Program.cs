using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;


namespace GC
{
    class Program
    {
        const string FileConfig = "config.ini";
        const string SectionConfig = "config";
        const string ShowFolderKey = "show_folder";
        const string LongNamesKey = "long_names";
        const string PrivilegeKey = "privilege";
        const string VersionKey = "version";
        bool ShowFolder = false;                                  // Запись папок в директориях (может увеличится размер лог файла)
        bool LongNames = false;                                   // Поддержка имен файлов более 260 символов (до 32000 поддерживает функция 2) 
        bool Privilege = false;                                   // !!!!!  Работа с привелегиями Backup (количество файлов может быть несколько миллионов, а размер лог файла более 1 Gb)
        int Version = 1;                                          // Версия функции поиска файлов и папок


        static int number = 1;
        static string unicode_version = "";

        #region Функции WinAPI

        const int SE_PRIVILEGE_ENABLED = 0x00000002;
        const int TOKEN_QUERY = 0x00000008;
        const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;
        const int ERROR_NOT_ALL_ASSIGNED = 1300;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES_SINGLE
        {
            public UInt32 PrivilegeCount;
            public LUID Luid;
            public UInt32 Attributes;
        }

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr htok, bool disall, ref TOKEN_PRIVILEGES_SINGLE newst, int len, IntPtr prev, IntPtr relen);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, UInt32 DesiredAccess, out IntPtr TokenHandle);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public long ftCreationTime;
            public long ftLastAccessTime;
            public long ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            private uint dwReserved0;
            private uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr FindFirstFile(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll")]
        static extern bool GetFileSizeEx(IntPtr hFile, out long lpFileSize);

        [DllImport("kernel32")]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);
        #endregion

        void Init()
        {
            string config;
            FileInfo fileInfo = new FileInfo(FileConfig);
            if(!fileInfo.Exists)
            {
                Console.WriteLine("Файл настроек '" + FileConfig + "' не найден, настройки выбрайки выбраны по умолчанию");
                return;
            }
            string Path = fileInfo.FullName.ToString();
            config = ReadINI(Path, SectionConfig, ShowFolderKey);
            if (config.Length > 0)
                ShowFolder = bool.Parse(config);

            config = ReadINI(Path, SectionConfig, LongNamesKey);
            if (config.Length > 0)
                LongNames = bool.Parse(config);

            config = ReadINI(Path, SectionConfig, PrivilegeKey);
            if (config.Length > 0)
                Privilege = bool.Parse(config);

            config = ReadINI(Path, SectionConfig, VersionKey);
            if (config.Length > 0)
                Version = int.Parse(config);
        }

        // Чтение ini-файла
        string ReadINI(string Path, string Section, string Key)
        {
            var RetVal = new StringBuilder(255);
            GetPrivateProfileString(Section, Key, "", RetVal, 255, Path);
            return RetVal.ToString();
        }

        // Получение привелегий Backup
        static bool RequestSetBackupPrivilege()
        {
            LUID luid;
            if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out luid))
                return false;

            IntPtr hToken;
            TOKEN_PRIVILEGES_SINGLE tp = new TOKEN_PRIVILEGES_SINGLE
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };
            return
                OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken) &&
                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) &&
                (Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED);
        }

        // Просмотр файлов и директорий
        #region Вариант 2
        struct ListFile
        {
            public string Name;
            public uint Size;
            public long Create;
            public ListFile(string Name, uint Size, long Create)
            {
                this.Name = Name;
                this.Create = Create;
                this.Size = Size;
            }
        }
        struct ListFolder
        {
            public string Name;
            public long Create;
            public ListFolder(string Name, long Create)
            {
                this.Name = Name;
                this.Create = Create;
            }
        }

        void ShowDirVersion2(StreamWriter log, string path)
        {
            WIN32_FIND_DATA findData;
            var ListFolders = new List<ListFolder>();
            var ListFiles = new List<ListFile>();
            IntPtr findHandle = FindFirstFile(Path.Combine(unicode_version + path, "*"), out findData);
            try
            {
                if (findHandle == INVALID_HANDLE_VALUE)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                do
                {
                    if (findData.cFileName == "." || findData.cFileName == "..")
                        continue;

                    if ((findData.dwFileAttributes & 0x10) == 0)
                        ListFiles.Add(new ListFile(findData.cFileName, findData.nFileSizeLow, findData.ftCreationTime));
                    else
                        ListFolders.Add(new ListFolder(findData.cFileName, findData.ftCreationTime));

                } while (FindNextFile(findHandle, out findData));
            }
            catch (Win32Exception)
            {
                File.AppendAllText("Error.txt", "Не удалось открыть или получить доступ к " +  path + "\r\n");
            }
            finally
            {
                FindClose(findHandle);
            }
            ListFolders.Sort((list1, list2) => (list1.Name.CompareTo(list2.Name)));                                        // Сортировка по алфавиту
            ListFiles.Sort((list1, list2) => (list1.Name.CompareTo(list2.Name)));
            if (ShowFolder)
            {
                for (int i = 0; i < ListFolders.Count; i++)
                {
                    log.WriteLine("\tПапка: " + ListFolders[i].Name + "\tDIR " + "\tСоздана: " + DateTime.FromFileTime(ListFolders[i].Create));
                }
            }
            float size;
            string str = "b";
            for (int i = 0; i < ListFiles.Count; i++)
            {
                size = ListFiles[i].Size;
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Kb";
                }
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Mb";
                }
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Gb";
                }
                log.WriteLine(number++ + "\tФайл: " + ListFiles[i].Name + "\tРазмер: " + size.ToString("f2") + str + " (" + ListFiles[i].Size + " byte)" + "\tСоздан: " + DateTime.FromFileTime(ListFiles[i].Create));
            }

            for (int i = 0; i < ListFolders.Count; i++)                                                            // Рекурсивный вход в папки
            {
                log.WriteLine("\tДиректория: " + ListFolders[i].Name + "\tDIR\tСоздана: " + DateTime.FromFileTime(ListFolders[i].Create) + "\tПолный путь: " + Path.Combine(path, ListFolders[i].Name));
                ShowDirVersion2(log, Path.Combine(path, ListFolders[i].Name));
            }
        }
        #endregion

        #region Вариант 1
        void ShowDirVersion1(StreamWriter log, string path)
        {
            var ListFolders = new List<string>();
            try
            {
                ListFolders.InsertRange(0, Directory.GetDirectories(path));
            }
            catch (UnauthorizedAccessException)
            {
                File.AppendAllText("Error.txt", "К директории " + path + " нет доступа\r\n");
                log.WriteLine("!!!\tК директории " + path + " нет доступа");
                return;
            }
            catch (PathTooLongException)
            {
                File.AppendAllText("Error.txt", "Слишком длинный путь к директории: " + path + "\r\n");
                log.WriteLine("!!!\tСлишком длинный путь к директории:\r\n" + path);
                return;
            }
            catch (IOException)
            {
                Console.WriteLine("В устройстве: " + path + " нет диска");
                log.WriteLine("!!!\tВ устройстве: " + path + " нет диска");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }
            if (ShowFolder)
            {
                for (int i = 0; i < ListFolders.Count; i++)
                {
                    log.WriteLine("\tПапка: " + ListFolders[i] + "\tDIR " + "\tСоздана: " + new DirectoryInfo(ListFolders[i]).CreationTime);
                }
            }
            float size;
            string str = "b";
            foreach (string file_name in Directory.GetFiles(path))
            {
                FileInfo file = new FileInfo(file_name);
                size = file.Length;
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Kb";
                }
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Mb";
                }
                if (size > 1000)
                {
                    size /= 1024;
                    str = "Gb";
                }
                log.WriteLine(number++ + "\tФайл: " + file.Name + "\tРазмер: " + size.ToString("f2") + str + " (" + file.Length + " byte)" + "\tСоздан: " + file.CreationTime);
            }
            for (int i = 0; i < ListFolders.Count; i++)                                                            // Рекурсивный вход в папки
            {
                log.WriteLine("\tДиректория: " + new DirectoryInfo(ListFolders[i]).Name + "\tDIR\tСоздана: " + new DirectoryInfo(ListFolders[i]).CreationTime + "\tПолный путь: " + ListFolders[i]);
                ShowDirVersion1(log, Path.Combine(path, ListFolders[i]));
            }
        }
        #endregion

        // Просмотр дисков
        void Show()
        {
            using (StreamWriter log = File.CreateText("Список всех файлов и директорий.txt"))
            {
                string information = "";
                if (Privilege)
                {
                    if (!RequestSetBackupPrivilege())
                        Console.WriteLine("Не удалось получить привелегии, доступ к некоторым папкам будет недоступен");
                }
                else
                    information += "без получения привелегий, ";

                if (LongNames)
                    unicode_version = @"\\?\";
                else
                    information += "без поддержки длинных имен(длина пути не должна превышать 248 символов, а имена файлов более 260 символов), ";

                if (!Privilege || !LongNames)
                    Console.WriteLine("Программа будет работать " + information + "доступ к некоторым папкам будет недоступен");

                if (!ShowFolder)
                    Console.WriteLine("Не будут записываться папки в директориях");
                Console.WriteLine("Все найденые файлы и папки будут записаны в файл \"Список всех файлов и директорий.txt\", ошибки будут записаны в файл Error.txt");
                try
                {
                    foreach (string disk in Directory.GetLogicalDrives())                                       // Перебор дисков
                    {
                        Console.WriteLine("Просмотр диска " + disk);
                        log.WriteLine("Диск: " + disk);
                        if (Version == 1)
                            ShowDirVersion1(log, disk);
                        else if (Version == 2)
                            ShowDirVersion2(log, disk);
                        else
                            Console.WriteLine("Неправильно выбраны настройки, параметр \"Version\" должен быть 1 или 2");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

            }
            Console.WriteLine("Просмотр всех файлов и папок в системе закончен, найдено " + number + " файлов");
            Console.ReadLine();
        }

        static void Main(string[] args)
        {
            Program program = new Program();
            program.Init();
            program.Show();
        }
    }
}