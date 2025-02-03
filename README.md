# Dextop

Dextop is a lightweight, screenshot-based remote desktop application written in C#. It consists of both server-side (host machine) and client-side (end-user) components, allowing for remote control through screenshots, mouse input, and keyboard events.

## Features

- **Keyboard and Mouse Input**
- **Multi-Monitor Support**
- **Screenshot-Based Streaming** (JPEG-based)

## Installation

### Common Setup

1. Clone the repository, using Git:
   ```sh
   git clone https://github.com/XeinTDM/Dextop.git
   ```
   Or download it manually using the `Code` -> `Download ZIP` button and extract it.
2. Open the `Dextop.sln` file in Visual Studio.

### **Server Setup**

1. Build and run `DextopServer`.

### Client Setup

1. Set the `ServerAddress` in `Program.cs` to the IP of the host machine.
2. Build and run `DextopClient`.

## Usage

1. Enable mouse and keyboard support using the UI buttons.
2. Use `F11` to toggle full-screen mode.
3. Adjust quality settings using the slider in the server UI.

## License

Dextop is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for more details.

## Disclaimer

This project is for educational and personal use only. Ensure that you have permission before accessing any remote machine.

