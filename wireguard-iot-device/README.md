# CYD IoT Demo Device

This project demonstrates an IoT use case for the Proxylity UDP Gateway using the "Cheap Yellow Display" (CYD) board. It combines temperature sensing, a touch GUI (LVGL), and secure UDP transport over a WireGuard tunnel. You can configure the device to use a WireGuard backend of your choice, including the [wireguard-echo](../wireguard-echo/readme.md) project also found in this repo.

## Features

- Temperature monitoring with DS18B20
- Touch-enabled LVGL GUI for controls and status
- WireGuard VPN for secure UDP transport
- Configurable operation via an on-device `config.txt` stored in LittleFS

## Hardware Requirements

### Cheap Yellow Display (CYD) Board
- ESP32-2432S028R development board
- Integrated 2.8" TFT display (240x320)
- Resistive touchscreen
- Built-in WiFi

### Additional Components
- DS18B20 temperature sensor
- 4.7kΩ pull-up resistor for OneWire bus
- Light Dependent Resistor (LDR) for ambient light sensing

## Pin Connections

| Component | Pin | GPIO |
|-----------|-----|------|
| DS18B20 Temperature Sensor | Data | GPIO 27 |
| Light Dependent Resistor | Analog | GPIO 34 |
| LCD Backlight | PWM | GPIO 21 |

## Software Dependencies

Recommended to use PlatformIO (VS Code). The project `platformio.ini` is preconfigured for an ESP32 environment and LittleFS.

Core libraries (installed via PlatformIO `lib_deps` or Arduino Library Manager):

- LVGL (v9+)
- TFT_eSPI by Bodmer
- XPT2046_Touchscreen (v1.4)
- OneWire
- DallasTemperature
- WireGuard-ESP32

Storage: LittleFS is used for on-device configuration storage (no SD card required).

## Configuration

Create a `config.txt` file in the root of the device filesystem (LittleFS). With PlatformIO, edit the `config.txt` in the project `data/` folder and upload using the PlatformIO filesystem upload command (see Installation).

Example `config.txt` format:

```
# WiFi Configuration
ssid_name: YourWiFiSSID
ssid_psk: YourWiFiPassword

# WireGuard Configuration
wg_private_key: your_device_private_key
wg_peer_public_key: server_public_key
wg_peer_endpoint: your.server.endpoint.com
wg_peer_port: 51820
```

Generate WireGuard keys with standard WireGuard tooling:

```bash
# Generate private key
wg genkey > privatekey

# Generate public key
wg pubkey < privatekey > publickey
```

## System Architecture

The firmware initializes in a simple state machine. Important states include:

1. STATE_INIT — load configuration from LittleFS
2. STATE_WIFI_BEGIN — connect to WiFi
3. STATE_TIME_CONFIG — sync time via NTP
4. STATE_WIREGUARD_BEGIN — initialize WireGuard
5. STATE_UDP_BEGIN — start UDP communication
6. STATE_READY — normal operation

## Installation

1. Hardware: assemble wiring per the pin connections above.
2. Development environment: install PlatformIO in VS Code (recommended) or use Arduino IDE.
3. Prepare configuration and files:
    - Place `config.txt` in `data/` (PlatformIO) or prepare a LittleFS image for Arduino IDE.
    - Ensure LVGL `lv_conf.h` and `TFT_eSPI` `User_Setup.h` are configured as required (see links below).
4. Upload filesystem and firmware (PlatformIO recommended):

    - Build firmware:

       platformio run

    - Upload LittleFS filesystem (uploads contents of the `data/` folder to the board):

       platformio run --target uploadfs

    - Upload firmware:

       platformio run --target upload

    You can also use the PlatformIO VS Code buttons: Build, Upload Filesystem Image, and Upload.

For Arduino IDE users, compile and upload the sketch as normal and use a LittleFS upload tool appropriate to your setup to flash the `config.txt` into the device filesystem.

### LVGL & TFT_eSPI configuration

- LVGL: use the `lv_conf.h` recommended for CYD compatibility (see Random Nerd Tutorials link in INSTALLATION).
- TFT_eSPI: replace `User_Setup.h` with the CYD-specific setup per the TFT_eSPI documentation.

## Usage

Power on the device and monitor serial output (115200). The device will read `config.txt` from LittleFS, connect to WiFi, establish the WireGuard tunnel, and begin sending UDP telemetry per configuration.

## Troubleshooting

- Configuration file not found: ensure `config.txt` is uploaded to LittleFS (PlatformIO: put in `data/` and run uploadfs).
- WiFi connection failed: verify SSID/PSK in `config.txt`.
- WireGuard issues: check keys and peer configuration.
- Sensor/display issues: verify wiring and the LVGL/TFT_eSPI configuration files.

## Security Considerations

- Keep device private keys and WiFi credentials secure. They are stored on-device in LittleFS.

## Links & Acknowledgments

- Random Nerd Tutorials — CYD LVGL and TFT_eSPI guidance
- LVGL project
- WireGuard project

## License

MIT — see the repository [LICENSE](../LICENSE) file for details.

---

UDP Gateway is a trademark of Proxylity LLC. WireGuard is a registered trademark of Jason A. Donenfeld.
