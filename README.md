# WinFIM.NET
WinFIM.NET - File Integrity Monitoring For Windows

For detail introduction, please visit my [Cyber Security Corner](https://redblueteam.wordpress.com/2020/03/11/winfim-net-windows-file-integrity-monitoring/) technical blog.

# Introduction
There are plenty of commercial tools to do file integrity monitoring (FIM). But, for freeware / Open Source, especially for Windows, it seems not much options.

I have developed a small Windows Service named [WinFIM.NET](https://github.com/redblueteam/WinFIM.NET) trying to fill up this gap.

# characteristics
The characteristics of this small application are:

- It will identify add / remove / modify of files and directories
- Monitoring scope could be easily customized
- Path exclusion (e.g. sub-directory) could be configured
- File extension exclusion could be configured (e.g. *.bak, *.tmp, *.log, *.mdf, *.ldf, *.xel, *. installlog)
- All the events are saved as native Windows Events, which could easily integrate with users' existing log management mechanism (e.g. Windows Event Subscription, Winlogbeat , nxlog, etc.)
- Deployment friendly
- Using SHA256 for hashing

# Installation (single machine)
1. Manual download all files to destination computer
2. Configure the parameters to fill your own environment
    1. `monlist.txt` – put your in-scope monitoring files / directories (Absolute path) line by line under this file
    2. `exclude_path.txt` – put your exclusion (Absolute path) line by line under this file (the exclusion should be overlapped with the paths in `monlist.txt` (e.g. Sub-directory of the in-scope directory)
    3. `exclude_extension.txt` – put all whitelisted file extension (normally, those extensions should be related to some frequent changing files, e.g. *.log, *.tmp)
    4. `scheduler.txt` – This file is to control whether the WinFIM.NET will be run in schedule mode or continuous mode.
        - Put a number `0` to the file, if you want the WinFIM.NET keep running.
        - Put a number (in minute) for the time separation of each run. e.g. 30 (that means file checksum will be run every 30 minutes).
3. Unblock the `WinFIM.NET Service.exe` (if required)
4. Install the Windows Service
    - Bring up an Administrator command prompt and navigate to the deployed folder, then execute `install_service.bat`
5. Verify if the Windows Service is up and running
6. Please make sure maximum log size is configured according to your deployment environment. By default, it only reserves around 1MB for it.
    - `%SystemRoot%\System32\Winevt\Logs\WinFIM.NET.evtx`
  
# Uninstallation
  Bring up an Administrator command prompt and navigate to the deployed folder, then execute `uninstall_service.bat`
  
# Windows Event IDs
There are 4 types of Windows event IDs:
- 7771 - remote connection status. Potentially useful for threat hunting if suspicious file changes are identified
- 7772 - service heartbeat message
- 7773 - errors
- 7776 - File / directory creation
- 7777 - File modification
- 7778 - File / directory deletion  
Enjoy!
 
# Development notes
- Source code available in Github project [OWASP/www-project-winfim.net](https://github.com/OWASP/www-project-winfim.net)
- Targets the .NET 4.8 framework
- The .mdb is Database is a SQL Server 2019 Local DB - it's not currently deployed
- The current database is a SQLite 3 database, and is built / tables created if not exist on program startup
- Is currently built with Visual Studio 2022
- Uses the Visual Studio Addon [Microsoft Visual Studio Installer Projects](https://marketplace.visualstudio.com/items?itemName=VisualStudioClient.MicrosoftVisualStudio2022InstallerProjects) to build the MSI installer

## Database structure
- Filename: fimdb.db
- database type: SQLite version 3
-Tables:
  - baseline_table
    - Stores the details of paths that were checked in the previous run. At the end of the current run, the contents are deleted then copied from current_table
  - conf_file_checksum
    - Stores checksums for the config files, e.g. monlist.txt, exclude_extension.txt, exclude_path.txt, monlist.txt
  - current_table
    - Stores the details of the the paths as they are being being checked, so it can be checked against the file details in the baseline_table. The contents are deleted at the end of the current run
  - monlist
    - Stores information about paths in the monlist table - useful to check if a path in monlist.txt was deleted since the last run, created since the last run

 Cheers
 
 Henry
