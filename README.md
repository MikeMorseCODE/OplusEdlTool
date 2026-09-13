# OPLUS EDL Tool (v2)

🌐 **Language:** [English](README.md) | [中文](README.zh.md)

> **⚠️ Version Notice:** This is **v2** of the tool.
>
> 🚀 **Latest Version:** The newer **v3** (closed-source) is available [here](https://static-tcdn.anteasy.com/xasdun/upload-log/oet-upload.html)

A cross-platform EDL (Emergency Download) flashing tool for OPLUS (OPPO/OnePlus/Realme) devices, built with Avalonia UI.

## What's New in v2

v2 is a complete rewrite with the following improvements:

- **New UI Framework**: Migrated from WPF to **Avalonia UI**
- **Better Architecture**: Cleaner codebase with improved maintainability
- **Improved Performance**: Faster partition parsing and flashing operations
- **Native AOT Compilation**: Single-file deployment, no .NET runtime required, faster startup

## Features

- **Enter Firehose Mode**: Load device programmer (devprg*.mbn) with digest and signature files
- **Flash ROM Packages**: Support for extracted ROM folders, OFP and OPS encrypted packages
- **Partition Management**: 
  - Read partition table from device
  - Read/Write/Erase individual partitions
  - Batch flash selected partitions
- **Super Partition Support**: Automatic merge of segmented super images (super.0.xxx.img, super.1.xxx.img, etc.), supports merging based on super_def.00000000.json definition
- **Multi-language Support**: English and Chinese interface
- **Auto Port Detection**: Automatic detection of Qualcomm 9008 EDL port

## Screenshots

### Read Partition Table
![Read Partition Table](Picture/ReadGPT.png)

### Backup Partitions
![Backup Partitions](Picture/ReadPartition.png)

## Requirements

- Windows 10/11 (x64)
- .NET 8.0 Runtime
- Qualcomm USB drivers installed

## Building from Source

### Prerequisites

- .NET 8.0 SDK
- Visual Studio 2022 or JetBrains Rider (optional)

### Build Commands

```bash
# Clone the repository
git clone https://github.com/salokrwhite/Oplus_EDL_Tool.git
cd Oplus_EDL_Tool

# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run

# Publish (AOT compiled)
dotnet publish -c Release
```

## Usage

### 1. Enter Firehose Mode

If your device is in EDL mode but not in Firehose mode:

1. Select the device programmer file (devprg*.mbn)
2. Select the digest file (*.bin)
3. Select the signature file (*.bin)
4. Click "Enter Firehose" button

### 2. Flash ROM Package

1. Click the folder button to select ROM source:
   - **Folder**: Select an extracted ROM folder containing IMAGES directory
   - **File**: Select OFP or OPS encrypted ROM file (will be automatically decrypted)
2. Select the rawprogram XML files to load
3. Click "Load" to parse partitions
4. Select partitions to flash (use checkbox)
5. Click "Start Flash" to begin flashing

### 3. Partition Operations

- **Read Partitions**: Read partition table from device
- **Read Selected**: Backup selected partitions to files
- **Write Selected**: Write selected partitions to device
- **Erase Selected**: Erase selected partitions

### Options

- **Export XML**: Export selected partitions to rawprogram XML when backing up
- **Protect LUN5**: Skip flashing partitions in rawprogram5.xml to protect LUN5
- **Auto Reboot**: Automatically reboot device after flashing

## Project Structure

```
OplusEdlTool/
├── App.axaml              # Application XAML
├── App.axaml.cs           # Application entry point
├── MainWindow.axaml       # Main window UI
├── MainWindow.axaml.cs    # Main window logic
├── AboutWindow.axaml      # About dialog
├── Services/
│   ├── EdlService.cs      # EDL communication service
│   ├── LanguageService.cs # Multi-language support
│   ├── OfpDecryptor.cs    # OFP file decryption
│   ├── OpsDecryptor.cs    # OPS file decryption
│   ├── GptParser.cs       # GPT partition table parser
│   ├── RawProgramXmlProcessor.cs  # rawprogram XML parser
│   ├── SuperMergeService.cs       # Super partition merge
│   └── ProcessRunner.cs   # External process runner
├── Tools/                 # External tools
│   ├── fh_loader.exe      # Qualcomm Firehose loader
│   ├── QSaharaServer.exe  # Qualcomm Sahara protocol server
│   ├── lsusb.exe          # USB device detection
│   ├── simg2img.exe       # Sparse image converter
│   └── lpmake.exe         # Dynamic partition (super) image creation tool
└── Fonts/                 # Custom fonts
```

## Dependencies

- [Avalonia UI](https://avaloniaui.net/) - Cross-platform UI framework
- [System.Management](https://www.nuget.org/packages/System.Management/) - WMI access for device detection

## Disclaimer

**USE AT YOUR OWN RISK!**

This tool is provided for educational and development purposes only. Flashing firmware can potentially brick your device. The authors are not responsible for any damage caused by using this tool.

- Always backup your data before flashing
- Make sure you have the correct firmware for your device
- Do not flash persist partition unless you know what you're doing

## License

This project is open source. See [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Acknowledgments

- Qualcomm for the Firehose protocol
- The Android development community
- Coolapk@MouZei
