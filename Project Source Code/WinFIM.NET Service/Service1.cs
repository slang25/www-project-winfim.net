﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Timers;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Data.SQLite;
using Serilog;

namespace WinFIM.NET_Service
{
    public partial class Service1 : ServiceBase
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int Wow64DisableWow64FsRedirection(ref IntPtr ptr);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int Wow64EnableWow64FsRedirection(ref IntPtr ptr);

        public Service1()
        {
            InitializeComponent();
            eventLog1 = new System.Diagnostics.EventLog();
            if (!System.Diagnostics.EventLog.SourceExists("WinFIM.NET"))
            {
                System.Diagnostics.EventLog.CreateEventSource(
                    "WinFIM.NET", "WinFIM.NET");
            }
            eventLog1.Source = "WinFIM.NET";
            eventLog1.Log = "WinFIM.NET";
        }

        // runs as a console application if a user interactively runs the "WinFIM.NET Service.exe" executable
        internal void TestStartupAndStop(string[] args)
        {
            ServiceStart();
            this.OnStop();
        }

        protected override void OnStart(string[] args)
        {
            Thread MyThread = new Thread(new ThreadStart(ServiceStart));
            MyThread.Name = "Worker Thread";
            MyThread.IsBackground = true;
            MyThread.Start();
        }

        private void ServiceStart()
        {
            //Read if there is any valid schedule timer (in minute)
            Initialise();
            string service_start_message;
            string workdir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string scheduler_conf = workdir + "\\scheduler.txt";
            int scheduler_min = 0;
            try
            {
                string timer_minute = File.ReadLines(scheduler_conf).First();
                timer_minute = timer_minute.Trim();
                scheduler_min = Convert.ToInt32(timer_minute);
            }
            catch (IOException e)
            {
                service_start_message = "Please check if the file 'scheduler.txt' exists or a numeric value is input into the file 'scheduler.txt'.\nIt will run in continuous mode.";
                Log.Error($"Exception: {e.Message} - {service_start_message}");
                eventLog1.WriteEntry($"Exception: {e.Message}\n{service_start_message}", EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                scheduler_min = 0;
            }

            if (scheduler_min > 0)
            //using timer mode
            {
                System.Timers.Timer timer = new System.Timers.Timer();
                timer.Interval = scheduler_min * 60000; // control the service to run every pre-defined minutes
                timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
                timer.Start();
                service_start_message = Properties.Settings.Default.service_start_message + ": (UTC) " + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "\n\n";
                service_start_message = service_start_message + GetRemoteConnections() + "\nThis service will run every " + scheduler_min.ToString() + " minute(s).";
                Log.Debug(service_start_message);
                eventLog1.WriteEntry(service_start_message, EventLogEntryType.Information, 7771); //setting the Event ID as 7771
                FileIntegrityCheck();
            }
            else
            //run in continuous mode
            {
                service_start_message = Properties.Settings.Default.service_start_message + ": (UTC) " + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "\n\n";
                service_start_message = service_start_message + GetRemoteConnections() + "\nThis service will run continuously.";
                Log.Debug(service_start_message);
                eventLog1.WriteEntry(service_start_message, EventLogEntryType.Information, 7771); //setting the Event ID as 7771
                bool tracker_boolean = true;
                while (tracker_boolean)
                {
                    tracker_boolean = FileIntegrityCheck();
                }
            }
        }


        private void OnTimer(object sender, ElapsedEventArgs args)
        {
            FileIntegrityCheck();
        }

        protected override void OnStop()
        {
            string service_stop_message = Properties.Settings.Default.service_stop_message + ": (UTC) " + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "\n\n";
            service_stop_message = service_stop_message + GetRemoteConnections() + "\n";
            Log.Information(service_stop_message);
            eventLog1.WriteEntry(service_stop_message, EventLogEntryType.Information, 7770); //setting the Event ID as 7770
        }

        //other functions

        //function for getting current remote connections mapping with users info on localhost
        private static string GetRemoteConnections()
        {
            string output = "ERROR in running CMD \"query user\"";
            try
            {
                using (Process process = new Process())
                {
                    IntPtr val = IntPtr.Zero;
                    Wow64DisableWow64FsRedirection(ref val);
                    process.StartInfo.FileName = @"cmd.exe";
                    process.StartInfo.Arguments = "/c \"@echo off & @for /f \"tokens=1,2,3,4,5\" %A in ('netstat -ano ^| findstr ESTABLISHED ^| findstr /v 127.0.0.1') do (@for /f \"tokens=1,2,5\" %F in ('qprocess \"%E\"') do (@IF NOT %H==IMAGE @echo %A , %B , %C , %D , %E , %F , %G , %H))\"";
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();
                    // Synchronously read the standard output of the spawned process. 
                    output = "Established Remote Connection (snapshot)" + "\n";
                    output = output + "========================================" + "\n" + "Proto | Local Address | Foreign Address | State | PID | USERNAME | SESSION NAME | IMAGE\n";
                    output = output + process.StandardOutput.ReadToEnd();
                    Log.Verbose(output);
                    process.WaitForExit();
                    Wow64EnableWow64FsRedirection(ref val);
                    return output;
                }

            }
            catch (Exception e)
            {
                string errorMessage = $"Error in GetRemoteConnections : {e.Message}";
                Log.Error(errorMessage );
                return output + "\n" + e.Message;
            }

        }

        //get file owner information
        private static string GetFileOwner(string path)
        {
            try
            {
                string file_owner = System.IO.File.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.NTAccount)).ToString();
                return file_owner;
            }
            catch (Exception e)
            {
                string errorMessage = $"Error in GetFileOwner - {e.Message} for path: {path}"; 
                Log.Error(errorMessage);
                return "UNKNOWN";
            }
        }

        //get file size information (MB)
        private static string GetFileSize(string path)
        {
            try
            {
                long length = new System.IO.FileInfo(path).Length;
                return Math.Round(Convert.ToDouble(length) / 1024 / 1024, 3).ToString();
            }
            catch (Exception e)
            {
                string errorMessage = "GetFileSize error";
                Log.Error(e, errorMessage);
                return "UNKNOWN";
            }
        }

        //get file extension exclusion list and construct regex
        //the return string will be "EMPTY", if there is no file extension exclusion
        private string ExcludeExtensionRegex()
        {
            try
            {
                string workdir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                string ext_exclude_list = workdir + "\\exclude_extension.txt";
                string[] lines = File.ReadAllLines(ext_exclude_list);
                lines = lines.Distinct().ToArray();
                string temp = "";
                List<string> ext_name = new List<string>() { };

                foreach (string line in lines) if (!string.IsNullOrWhiteSpace(line))
                    {
                        temp = line.TrimEnd('\r', '\n');

                        var match = Regex.Match(temp, @"/^[a-zA-Z0-9-_]+$/", RegexOptions.IgnoreCase);
                        //if the file extension does not match the exclusion
                        if (!match.Success)
                        {
                            Log.Verbose("Regex success: " + temp);
                            temp = "[.]" + temp;
                            ext_name.Add(temp);
                        }
                        else
                        {
                            string errorMessage = "Extension \"" + temp + "\" is invalid, file extension should be alphanumeric and '_' + '-' only.";
                            Log.Error(errorMessage);
                            eventLog1.WriteEntry(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                        }
                    }

                bool isEmpty = !ext_name.Any();
                if (isEmpty)
                {
                    return "EMPTY";
                }
                else
                {
                    var result = String.Join("|", ext_name.ToArray());
                    string regex = "^.*(" + result + ")$";
                    return regex;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "ExcludeExtensionRegex");
                return "ERROR";
            }
        }

        //Compute file Hash in Sha256 return with a byte value.
        //for usage (return string of Sha256 file hash): BytesToString(GetHashSha256(filename))
        private readonly SHA256 Sha256 = SHA256.Create();
        private byte[] GetHashSha256(string filename)
        {

            Stream stream = new FileStream(filename, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);
            byte[] temp_result = Sha256.ComputeHash(stream);
            stream.Close();
            return temp_result;
        }

        // Return a byte array as a sequence of hex values.
        private static string BytesToString(byte[] bytes)
        {
            string result = "";
            foreach (byte b in bytes) result += b.ToString("x2");
            return result;
        }

        private void Initialise()
        {
            string workdir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            //using SQLite connection
            string dbfile = workdir + "\\fimdb.db";
            string cs = @"URI=file:" + dbfile + ";PRAGMA journal_mode=WAL;";

            SQLiteHelper.EnsureDatabaseExists(dbfile);
            SQLiteHelper.EnsureTablesExist(cs);

            string ex_ext_hash = "";
            string ex_path_hash = "";
            string mon_hash = "";

            try
            {
                //create checksum for config files: exclude_extension.txt | exclude_path.txt | monlist.txt
                ex_ext_hash = BytesToString(GetHashSha256(workdir + "\\exclude_extension.txt"));
                ex_path_hash = BytesToString(GetHashSha256(workdir + "\\exclude_path.txt"));
                mon_hash = BytesToString(GetHashSha256(workdir + "\\monlist.txt"));
            }
            catch (Exception e)
            {
                string message = "Exception: " + e.Message + "\nConfig files: exclude_extension.txt | exclude_path.txt | monlist.txt is / are missing or having issue to access.";
                Log.Error(message);
                eventLog1.WriteEntry(message, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
            }


            //compare the checksum with those stored in the DB
            String sql, output = "";

            //SQLite
            SQLiteConnection con;
            SQLiteCommand command;
            SQLiteDataReader dataReader;
            Boolean have_hash = false;


            try
            {
                //SQLite connection
                con = new SQLiteConnection(cs);

                con.Open();
                //check if the baseline table is empty (If count is 0 then the table is empty.)
                sql = "SELECT COUNT(*) FROM conf_file_checksum";
                Log.Verbose(sql);
                command = new SQLiteCommand(sql, con);
                dataReader = command.ExecuteReader();
                if (dataReader.Read())
                {
                    output = dataReader.GetValue(0).ToString();
                    Log.Verbose("Output count conf file hash: " + output);
                    dataReader.Close();
                }

                if (!output.Equals("3")) //suppose there should be 3 rows, if previous checksum exist
                {

                    //no checksum or incompetent checksum, empty all table
                    sql = "DELETE FROM conf_file_checksum";
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql = "DELETE FROM baseline_table";
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql = "DELETE FROM current_table";
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    //insert the current hash to DB
                    sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\exclude_extension.txt','" + ex_ext_hash + "')";
                    Log.Verbose(sql);
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\exclude_path.txt','" + ex_path_hash + "')";
                    Log.Verbose(sql);
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\monlist.txt','" + mon_hash + "')";
                    Log.Verbose(sql);
                    command = new SQLiteCommand(sql, con);
                    try
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    con.Close();
                    con.Dispose();
                }
                else
                {
                    //else compare the checksum, if difference, store the new checksum into DB, and empty both baseline_table and current_table
                    int temp_count = 0;

                    sql = "SELECT filehash FROM conf_file_checksum WHERE filename='" + workdir + "\\exclude_extension.txt" + "'";
                    command = new SQLiteCommand(sql, con);
                    dataReader = command.ExecuteReader();
                    if (dataReader.Read())
                    {
                        output = dataReader.GetValue(0).ToString();
                        dataReader.Close();
                        if (output.Equals(ex_ext_hash))
                        {
                            temp_count = temp_count + 1;
                        }
                    }

                    sql = "SELECT filehash FROM conf_file_checksum WHERE filename='" + workdir + "\\exclude_path.txt" + "'";
                    command = new SQLiteCommand(sql, con);
                    dataReader = command.ExecuteReader();
                    if (dataReader.Read())
                    {
                        output = dataReader.GetValue(0).ToString();
                        dataReader.Close();
                        if (output.Equals(ex_path_hash))
                        {
                            temp_count = temp_count + 1;
                        }
                    }

                    sql = "SELECT filehash FROM conf_file_checksum WHERE filename='" + workdir + "\\monlist.txt" + "'";
                    command = new SQLiteCommand(sql, con);
                    dataReader = command.ExecuteReader();
                    if (dataReader.Read())
                    {
                        output = dataReader.GetValue(0).ToString();
                        dataReader.Close();
                        if (output.Equals(mon_hash))
                        {
                            temp_count = temp_count + 1;
                        }
                    }
                    Log.Verbose("Temp Same Count: " + temp_count.ToString());

                    //if all hashes are the same
                    if (temp_count == 3)
                    {
                        //use the same config
                    }
                    else
                    {
                        //clear all tables
                        sql = "DELETE FROM conf_file_checksum";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }

                        sql = "DELETE FROM baseline_table";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }

                        sql = "DELETE FROM current_table";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }

                        //insert the current hash to DB
                        sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\exclude_extension.txt','" + ex_ext_hash + "')";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }

                        sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\exclude_path.txt','" + ex_path_hash + "')";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }

                        sql = "Insert into conf_file_checksum (filename, filehash) values('" + workdir + "\\monlist.txt','" + mon_hash + "')";
                        Log.Verbose(sql);
                        command = new SQLiteCommand(sql, con);
                        try
                        {
                            command.ExecuteNonQuery();
                            command.Dispose();
                        }
                        catch (Exception e)
                        {
                            string errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }
                    }
                    con.Close();
                    con.Dispose();

                }

            }
            catch (Exception e)
            {
                Log.Error(e, e.Message);
                con = new SQLiteConnection(cs);
                con.Open();
                sql = "DELETE FROM conf_file_checksum";
                command = new SQLiteCommand(sql, con);
                try
                {
                    command.ExecuteNonQuery();
                    command.Dispose();
                }
                catch (Exception e1)
                {
                    string errorMessage = "SQLite Exception: " + e.Message;
                    Log.Error(errorMessage);
                }
                command.Dispose();
                con.Close();
                con.Dispose();
            }

        }

        private bool CheckIfMonListBasePathExists(string cs, string line)
        {
            string message;
            if (Directory.Exists(line) || File.Exists(line))
            {
                string ret = SQLiteHelper.QueryMonListTable(cs, line);
                //if base path doesn't exist in SQLite table monlist
                if (string.IsNullOrEmpty(ret))
                {
                    SQLiteHelper.InsertOrReplaceInMonListTable(cs, line, true);
                    message = $"{line} exists - adding to monlist table";
                    Log.Warning(message);
                    eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7776);
                }

                else if (ret == "True")
                {
                    Log.Debug($"{line} still exists");
                }
                return true;
            }
            else
            {
                string ret = SQLiteHelper.QueryMonListTable(cs, line);
                if (string.IsNullOrEmpty(ret))
                {
                    SQLiteHelper.InsertOrReplaceInMonListTable(cs, line, false);
                    message = $"{line} does not exist";
                    Log.Warning(message);
                    eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7778);
                }

                else if (ret == "True")
                {
                    SQLiteHelper.InsertOrReplaceInMonListTable(cs, line, false);
                    message = $"{line} has been deleted";
                    Log.Warning(message);
                    eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7778);
                }
                else
                {
                    Log.Debug($"{line} still does not exist");
                }
                return false;
            }
        }

        //function for file integrity checking
        private bool FileIntegrityCheck()
        {
            string workdir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            string dbfile = workdir + "\\fimdb.db";
            string cs = @"URI=file:" + dbfile + ";PRAGMA journal_mode=WAL;";

            SQLiteConnection con;
            con = new SQLiteConnection(cs);

            SQLiteCommand command;
            SQLiteDataReader dataReader;

            string sql, output = "";
            bool have_baseline = false;
            try
            {
                con.Open();
                //check if the baseline table is empty (If count is 0 then the table is empty.)
                sql = "SELECT COUNT(*) FROM baseline_table";
                Log.Verbose(sql);
                command = new SQLiteCommand(sql, con);
                dataReader = command.ExecuteReader();
                if (dataReader.Read())
                {
                    output = dataReader.GetValue(0).ToString();
                }
                dataReader.Close();

                if (!output.Equals("0"))
                {
                    have_baseline = true;
                }
                else
                {
                    have_baseline = false;
                }
            }
            catch (Exception e)
            {
                string errorMessage = "Exception : " + e.Message + "\nPlease make sure local database file \"fimdb.db\" exists.";
                Log.Error(errorMessage);
                eventLog1.WriteEntry(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                return false;
            }

            //debug - printout the sql return result
            Log.Verbose(output);

            //create variable to store the full file list
            string filelist = "";
            string exfilelist = "";

            //temp variable to store the file attributes for file or directory
            FileAttributes attr;

            //create a stop watch the time consumed by the whole process
            Stopwatch watch = new Stopwatch();
            watch.Start();

            //read the monitoring list (line by line)
            string monlist = workdir + "\\monlist.txt";
            string[] lines;
            try
            {
                lines = File.ReadAllLines(monlist);
            }
            catch (Exception e)
            {
                string errorMessage = "Exception : " + e.Message + "\nPlease make sure all input entries are correct under \"monlist.txt\".\nPlease restart the service after correction.";
                Log.Error(errorMessage);
                eventLog1.WriteEntry(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                return false;
            }


            try
            {
                Log.Information("Starting checks");
                //get the full file mon list for further processing
                foreach (string line in lines) if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (!(CheckIfMonListBasePathExists(cs, line)))
                        {
                            continue;
                        }
                        
                        //1. check the line entry is a file or a directory
                        attr = File.GetAttributes(line);
                        if (attr.HasFlag(FileAttributes.Directory))
                        {
                            //2. if it is a directory
                            //try to use cmd to get a full files (including hidden files) and all level of sub-directories list from a directory
                            using (Process process = new Process())
                            {
                                process.StartInfo.FileName = @"cmd.exe";
                                process.StartInfo.Arguments = @"/c dir /a: /b /s " + "\"" + line + "\"";
                                process.StartInfo.CreateNoWindow = true;
                                process.StartInfo.UseShellExecute = false;
                                process.StartInfo.RedirectStandardOutput = true;
                                process.Start();
                                filelist = filelist + line + "\n"; //make sure the most outer directory is included in the list
                                filelist = filelist + process.StandardOutput.ReadToEnd();
                                process.WaitForExit();
                            }
                        }
                        else
                        {
                            //3. if it is a file
                            filelist = filelist + line + "\n";
                        }
                    }

                //change all string in filelist to lowercase for easy comparison to exclusion list
                filelist = filelist.ToLower();
                filelist = filelist.TrimEnd('\r', '\n');
                Log.Verbose(filelist);

                //read the exclude list (line by line)
                string excludelist = workdir + "\\exclude_path.txt";
                try
                {
                    lines = File.ReadAllLines(excludelist);
                }
                catch (Exception e)
                {
                    string errorMessage = "Exception : " + e.Message + "\nPlease make sure all input entries are correct under \"exclude_path.txt\".\nPlease restart the service after correction.";
                    Log.Error(errorMessage);
                    eventLog1.WriteEntry(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                    return false;
                }


                //get the full exclude file list for further processing
                foreach (string line in lines) if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            //1. check the line entry is a file or a directory
                            attr = File.GetAttributes(line);
                            if (attr.HasFlag(FileAttributes.Directory))
                            {
                                //2. if it is a directory
                                //try to use cmd to get a full files (including hidden files) and all level of sub-directories list from a directory
                                using (Process process = new Process())
                                {
                                    process.StartInfo.FileName = @"cmd.exe";
                                    process.StartInfo.Arguments = @"/c dir /a: /b /s " + "\"" + line + "\"";
                                    process.StartInfo.CreateNoWindow = true;
                                    process.StartInfo.UseShellExecute = false;
                                    process.StartInfo.RedirectStandardOutput = true;
                                    process.Start();
                                    exfilelist = exfilelist + line + "\n"; //make sure the most outter directory is included in the list
                                    exfilelist = exfilelist + process.StandardOutput.ReadToEnd();
                                    process.WaitForExit();
                                }
                            }
                            else
                            {
                                //3. if it is a file
                                exfilelist = exfilelist + line + "\n";
                            }

                        }
                        catch (Exception e)
                        {
                            string errorMessage = "Exclusion error:" + e.Message;
                            Log.Error(errorMessage);
                            //The file path on the exclusion could be not exist
                        }


                    }
                //change all string in exfilelist to lowercase for easy comparsion to exclusion list
                exfilelist = exfilelist.ToLower();
                exfilelist = exfilelist.TrimEnd('\r', '\n');
                Log.Verbose(exfilelist);

                //convert filelist string to string array
                string[] filelist_array = filelist.Split(
                    new[] { "\r\n", "\r", "\n" },
                    StringSplitOptions.None
                );
                //remove duplicate element in filelist_array
                filelist_array = filelist_array.Distinct().ToArray();

                //convert exfilelist string to string array
                string[] exfilelist_array = exfilelist.Split(
                    new[] { "\r\n", "\r", "\n" },
                    StringSplitOptions.None
                );
                //remove duplicate element in exfilelist_array
                exfilelist_array = exfilelist_array.Distinct().ToArray();

                //filter exclusion file list
                IEnumerable<string> final_filelist = filelist_array.Except(exfilelist_array);

                //get the regex of file extension exclusion
                string regex = ExcludeExtensionRegex();
                Log.Verbose("File Extension Exclusion REGEX:" + regex);

                //After the full filelist is collected, checksum need to be conducted for file (not directory) after further file extension filtering is done
                //This is for debug purpose to test checksum output
                string line_output = "";
                string temp_hash = "";

                //setup another DB connection to access the baseline_table
                SQLiteConnection con2;
                con2 = new SQLiteConnection(cs);
                SQLiteCommand command2;
                SQLiteDataReader dataReader2;
                string sql2, output2 = "";
                con2.Open();

                foreach (string s in final_filelist)
                {
                    string message = string.Empty;
                    Log.Verbose(s);
                    try
                    {
                        //1. check the line entry is a file or a directory
                        attr = File.GetAttributes(s);
                        if (attr.HasFlag(FileAttributes.Directory))
                        {
                            //2. if it is a directory, no need to do checksum
                            line_output = s + " | Directory | " + " file size = N/A | files owner = " + GetFileOwner(s) + " | file hash = N/A";
                            //if there is content in baseline_table before, write to current_table
                            if (have_baseline)
                            {
                                sql = "Insert into current_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "',0,'" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','NA','Directory')";
                                Log.Verbose(sql);
                                command = new SQLiteCommand(sql, con);
                                try
                                {
                                    command.ExecuteNonQuery();
                                    command.Dispose();
                                }
                                catch (Exception e)
                                {
                                    string errorMessage = "SQLite Exception: " + e.Message;
                                    Log.Error(errorMessage);
                                }


                                //compare with baseline_table
                                //1. check if the file exist in baseline_table
                                sql2 = "SELECT COUNT(*) FROM baseline_table WHERE filename='" + s + "'";
                                command2 = new SQLiteCommand(sql2, con2);
                                dataReader2 = command2.ExecuteReader();
                                if (dataReader2.Read())
                                {
                                    output2 = dataReader2.GetValue(0).ToString();
                                }
                                dataReader2.Close();
                                if (!output2.Equals("0"))
                                {
                                    Log.Verbose("Directory :'" + s + "' has no change.");
                                }
                                else
                                {
                                    message = "Directory :'" + s + "' is newly created.\nOwner: " + GetFileOwner(s);
                                    Log.Warning(message);
                                    eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                                }
                                dataReader2.Close();
                            }
                            //if there is no content in baseline_table, write to baseline_table instead
                            else
                            {
                                sql = "Insert into baseline_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "',0,'" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','NA','Directory')";
                                Log.Verbose(sql);
                                command = new SQLiteCommand(sql, con);
                                try
                                {
                                    command.ExecuteNonQuery();
                                    command.Dispose();
                                }
                                catch (Exception e)
                                {
                                    string errorMessage = "SQLite Exception: " + e.Message;
                                    Log.Error(errorMessage);
                                }
                            }
                            Log.Verbose(line_output);
                        }
                        else
                        {
                            //3. if it is a file
                            //a. if there is file extension exclusion
                            if (regex.Equals("EMPTY"))
                            {
                                try
                                {
                                    temp_hash = BytesToString(GetHashSha256(s));
                                }
                                catch (Exception e)
                                {
                                    temp_hash = "UNKNOWN";
                                    message = "File '" + s + "' is locked and not accessible for Hash calculation.";
                                    Log.Error(message);
                                    eventLog1.WriteEntry(message, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                                }
                                line_output = s + " | File | " + " file size = " + GetFileSize(s) + " | files owner = " + GetFileOwner(s) + " | file hash = " + temp_hash;
                                //if there is content in baseline_table before, write to current_table
                                if (have_baseline)
                                {
                                    sql = "Insert into current_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "','" + GetFileSize(s) + "','" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','" + temp_hash + "','File')";
                                    Log.Verbose(sql);
                                    command = new SQLiteCommand(sql, con);
                                    try
                                    {
                                        command.ExecuteNonQuery();
                                        command.Dispose();
                                    }
                                    catch (Exception e)
                                    {
                                        string errorMessage = "SQLite Exception: " + e.Message;
                                        Log.Error(errorMessage);
                                    }

                                    //compare with baseline_table
                                    //1. check if the file exist in baseline_table
                                    sql2 = "SELECT COUNT(*) FROM baseline_table WHERE filename='" + s + "'";
                                    command2 = new SQLiteCommand(sql2, con2);
                                    dataReader2 = command2.ExecuteReader();
                                    if (dataReader2.Read())
                                    {
                                        output2 = dataReader2.GetValue(0).ToString();
                                    }
                                    dataReader2.Close();
                                    if (!output2.Equals("0"))
                                    {
                                        //1. check if the file hash in baseline_table changed
                                        sql2 = "SELECT COUNT(*) FROM baseline_table WHERE filename='" + s + "' AND filehash='" + temp_hash + "'";
                                        command2 = new SQLiteCommand(sql2, con2);
                                        dataReader2 = command2.ExecuteReader();
                                        if (dataReader2.Read())
                                        {
                                            output2 = dataReader2.GetValue(0).ToString();
                                        }
                                        dataReader2.Close();
                                        if (!output2.Equals("0"))
                                        {
                                            Log.Verbose("File :'" + s + "' has no change.");
                                        }
                                        else
                                        {
                                            sql2 = "SELECT filename, filesize, fileowner, filehash, checktime FROM baseline_table WHERE filename='" + s + "'";
                                            command2 = new SQLiteCommand(sql2, con2);
                                            dataReader2 = command2.ExecuteReader();
                                            if (dataReader2.Read())
                                            {
                                                message = "File :'" + s + "' is modified. \nPrevious check at:" + dataReader2.GetValue(4).ToString() + "\nFile hash: (Previous)" + dataReader2.GetValue(3).ToString() + " (Current)" + temp_hash + "\nFile Size: (Previous)" + dataReader2.GetValue(1).ToString() + "MB (Current)" + GetFileSize(s) + "MB\nFile Owner: (Previous)" + dataReader2.GetValue(2).ToString() + " (Current)" + GetFileOwner(s);
                                                Log.Warning(message);
                                                eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7777); //setting the Event ID as 7777
                                            }
                                            dataReader2.Close();
                                        }
                                    }
                                    else
                                    {
                                        message = "File :'" + s + "' is newly created.\nOwner: " + GetFileOwner(s) + " Hash:" + temp_hash;
                                        Log.Warning(message);
                                        eventLog1.WriteEntry("File :'" + s + "' is newly created.\nOwner: " + GetFileOwner(s) + " Hash:" + temp_hash, EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                                    }
                                    dataReader2.Close();
                                    command2.Dispose();
                                }
                                //if there is no content in baseline_table, write to baseline_table instead
                                else
                                {
                                    sql = "Insert into baseline_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "'," + GetFileSize(s) + ",'" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','" + temp_hash + "','File')";
                                    Log.Verbose(sql);
                                    command = new SQLiteCommand(sql, con);
                                    try
                                    {
                                        command.ExecuteNonQuery();
                                        command.Dispose();
                                    }
                                    catch (Exception e)
                                    {
                                        string errorMessage = "SQLite Exception: " + e.Message;
                                        Log.Error(errorMessage);
                                    }
                                }
                                Log.Verbose(line_output);
                            }
                            //b. if there is file extension exclusion
                            else
                            {
                                var match = Regex.Match(s, regex, RegexOptions.IgnoreCase);
                                //if the file extension does not match the exclusion
                                if (!match.Success)
                                {
                                    try
                                    {
                                        temp_hash = BytesToString(GetHashSha256(s));
                                    }
                                    catch (Exception e)
                                    {
                                        temp_hash = "UNKNOWN";
                                        message = "File '" + s + "' is locked and not accessible for Hash calculation.";
                                        Log.Error(message);
                                        eventLog1.WriteEntry(message, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                                    }
                                    line_output = s + " | File | " + " file size = " + GetFileSize(s) + " | files owner = " + GetFileOwner(s) + " | file hash = " + temp_hash;
                                    if (have_baseline)
                                    {
                                        sql = "Insert into current_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "'," + GetFileSize(s) + ",'" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','" + temp_hash + "','File')";
                                        Log.Verbose(sql);
                                        command = new SQLiteCommand(sql, con);
                                        try
                                        {
                                            command.ExecuteNonQuery();
                                            command.Dispose();
                                        }
                                        catch (Exception e)
                                        {
                                            message = "SQLite Exception: " + e.Message;
                                            Log.Error(message);
                                        }

                                        //compare with baseline_table
                                        //1. check if the file exist in baseline_table
                                        sql2 = "SELECT COUNT(*) FROM baseline_table WHERE filename='" + s + "'";
                                        command2 = new SQLiteCommand(sql2, con2);
                                        dataReader2 = command2.ExecuteReader();
                                        if (dataReader2.Read())
                                        {
                                            output2 = dataReader2.GetValue(0).ToString();
                                        }
                                        dataReader2.Close();
                                        if (!output2.Equals("0"))
                                        {
                                            //1. check if the file hash in baseline_table changed
                                            sql2 = "SELECT COUNT(*) FROM baseline_table WHERE filename='" + s + "' AND filehash='" + temp_hash + "'";
                                            command2 = new SQLiteCommand(sql2, con2);
                                            dataReader2 = command2.ExecuteReader();
                                            if (dataReader2.Read())
                                            {
                                                output2 = dataReader2.GetValue(0).ToString();
                                            }
                                            dataReader2.Close();
                                            if (!output2.Equals("0"))
                                            {
                                                Log.Verbose("File :'" + s + "' has no change.");
                                            }
                                            else
                                            {
                                                sql2 = "SELECT filename, filesize, fileowner, filehash, checktime FROM baseline_table WHERE filename='" + s + "'";
                                                command2 = new SQLiteCommand(sql2, con2);
                                                dataReader2 = command2.ExecuteReader();
                                                if (dataReader2.Read())
                                                {
                                                    message = "File :'" + s + "' is modified. Previous check at:" + dataReader2.GetValue(4).ToString() + "\nFile hash: (Previous)" + dataReader2.GetValue(3).ToString() + " (Current)" + temp_hash + "\nFile Size: (Previous)" + dataReader2.GetValue(1).ToString() + "MB (Current)" + GetFileSize(s) + "MB\nFile Owner: (Previous)" + dataReader2.GetValue(2).ToString() + " (Current)" + GetFileOwner(s);
                                                    Log.Warning(message);
                                                    eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7777); //setting the Event ID as 7777
                                                }
                                                dataReader2.Close();
                                            }
                                        }
                                        else
                                        {
                                            message = "File :'" + s + "' is newly created.\nOwner: " + GetFileOwner(s) + " Hash:" + temp_hash;
                                            Log.Warning(message);
                                            eventLog1.WriteEntry(message, EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                                        }
                                        dataReader2.Close();
                                        command2.Dispose();
                                    }
                                    //if there is no content in baseline_table, write to baseline_table instead
                                    else
                                    {
                                        sql = "Insert into baseline_table (filename, filesize, fileowner, checktime, filehash, filetype) values('" + s + "'," + GetFileSize(s) + ",'" + GetFileOwner(s) + "','(UTC)" + DateTime.UtcNow.ToString(@"M/d/yyyy hh:mm:ss tt") + "','" + temp_hash + "','File')";
                                        command = new SQLiteCommand(sql, con);
                                        try
                                        {
                                            command.ExecuteNonQuery();
                                            command.Dispose();
                                        }
                                        catch (Exception e)
                                        {
                                            string errorMessage = "SQLite Exception: " + e.Message;
                                            Log.Error(errorMessage);
                                        }
                                    }
                                    Log.Verbose(line_output);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        message = "File '" + s + "' could be renamed / deleted during the hash calculation. This file is ignored in this checking cycle.";
                        Log.Error(message);
                        eventLog1.WriteEntry(message, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                    }

                }
                
                //final check if any files / directory is deleted
                if (have_baseline)
                {
                    sql2 = "SELECT baseline_table.filename FROM baseline_table LEFT JOIN current_table ON baseline_table.filename = current_table.filename WHERE current_table.filename IS NULL";
                    command2 = new SQLiteCommand(sql2, con2);
                    dataReader2 = command2.ExecuteReader();
                    output2 = "";
                    while (dataReader2.Read())
                    {
                        output2 = dataReader2.GetValue(0).ToString();
                        string deleted_message = "The file / directory '" + output2 + "' is deleted.";
                        eventLog1.WriteEntry(deleted_message, EventLogEntryType.Warning, 7778); //setting the Event ID as 7778
                    }
                    dataReader2.Close();
                    //delete all rows in baseline_table, copy all rows from current_table to baseline_table, then clear current_table
                    sql2 = "DELETE FROM baseline_table WHERE filename IS NOT NULL";
                    command2 = new SQLiteCommand(sql2, con2);
                    try
                    {
                        command2.ExecuteNonQuery();
                        command2.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql2 = "INSERT INTO baseline_table SELECT * FROM current_table";
                    command2 = new SQLiteCommand(sql2, con2);
                    try
                    {
                        command2.ExecuteNonQuery();
                        command2.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                    sql2 = "DELETE FROM current_table WHERE filename IS NOT NULL";
                    command2 = new SQLiteCommand(sql2, con2);
                    try
                    {
                        command2.ExecuteNonQuery();
                        command2.Dispose();
                    }
                    catch (Exception e)
                    {
                        string errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }

                }

                //local database close connection
                command.Dispose();
                con2.Close();
                con.Close();

                watch.Stop();
                string stop_message = "Total time consumed in this round file integrity checking  = " + watch.ElapsedMilliseconds + "ms (" + Math.Round(Convert.ToDouble(watch.ElapsedMilliseconds) / 1000, 3).ToString() + "s).\n" + GetRemoteConnections();
                Log.Debug(stop_message);
                eventLog1.WriteEntry(stop_message, EventLogEntryType.Information, 7771); //setting the Event ID as 7771
                Log.Verbose("Total time consumed in this round file integrity checking  = " + watch.ElapsedMilliseconds + "ms (" + Math.Round(Convert.ToDouble(watch.ElapsedMilliseconds)/1000,3).ToString() + "s).");
                return true;

            }
            catch (Exception e)
            {
                string errorMessage = "Exception : " + e.Message + "\nPlease make sure all input entries are correct under \"monlist.txt\", \"exclude_path.txt\" and \"exclude_extension.txt\".\nPlease restart the service after correction.";
                Log.Error(errorMessage);
                eventLog1.WriteEntry(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                return false;
            }

        }

    }
}
