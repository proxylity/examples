# Installation and Setup Guide

## Prerequisites

### Hardware
- ESP32-2432S028R "Cheap Yellow Display" board
- DS18B20 temperature sensor
- 4.7kΩ resistor (for OneWire pullup)
- USB-C cable for programming

### Software
- Arduino IDE 2.0 or later
- ESP32 board package installed (version 2.0.17)
 - PlatformIO extension for VS Code (recommended) or Arduino IDE 2.0
 - ESP32 board package installed (when using Arduino IDE)

## Step-by-Step Installation

### 1. Arduino IDE Setup
1. **PlatformIO (recommended) / Arduino IDE**:

- PlatformIO: Install the PlatformIO extension in VS Code. This project includes a `platformio.ini` preconfigured for an ESP32 environment. PlatformIO also supports building and uploading a LittleFS image separately from the firmware (see "Upload Filesystem & Firmware" below).

- Arduino IDE: If you prefer Arduino IDE, follow the ESP32 board package installation steps:
   - Open Arduino IDE
   - Go to File → Preferences
   - Add to Additional Board Manager URLs:
      ```
      https://dl.espressif.com/dl/package_esp32_index.json
      ```
   - Go to Tools → Board → Boards Manager
   - Search for "ESP32" and install the package
   - Tools → Board → ESP32 Dev Module
   - Tools → Partition Scheme → Default 4MB with spiffs (or appropriate LittleFS partition)

### 2. Library Installation

Install the following libraries via Library Manager (Tools → Manage Libraries) or via PlatformIO `lib_deps`:

```
1. LVGL by kisvegabor (version 9.2)
2. TFT_eSPI by Bodmer
3. XPT2046_Touchscreen by Paul Stoffregen  
4. OneWire by Jim Studt
5. DallasTemperature by Miles Burton
6. WireGuard-ESP32
```

### 3. Critical Configuration Files

#### LVGL Configuration
1. Download the correct `lv_conf.h` from:
   https://RandomNerdTutorials.com/cyd-lvgl/
2. Place it in your Arduino libraries folder:
   - Windows: `Documents\Arduino\libraries\lvgl\lv_conf.h`
   - macOS: `~/Documents/Arduino/libraries/lvgl/lv_conf.h`
   - Linux: `~/Arduino/libraries/lvgl/lv_conf.h`

#### TFT_eSPI Configuration  
1. Download the correct `User_Setup.h` for CYD from:
   https://RandomNerdTutorials.com/esp32-tft-lvgl/
2. Replace the file in:
   `Arduino\libraries\TFT_eSPI\User_Setup.h`

### 4. Hardware Assembly

**Connect DS18B20**:
```
DS18B20 Pin 1 (GND) → CYD GND
DS18B20 Pin 2 (Data) → CYD GPIO 27
DS18B20 Pin 3 (VDD) → CYD 3.3V
Add 4.7kΩ resistor between Data and VDD pins
```

### 6. Generate WireGuard Keys

Using WireGuard tools:
```bash
# Generate private key
wg genkey > private.key

# Generate public key
wg pubkey < private.key > public.key

# Display keys
cat private.key  # Use this in config.txt
cat public.key   # Configure this on your UDP Gateway Listener
```

### 7. Set Up Proxylity UDP Gateway

Configure your Proxylity UDP Gateway WireGuard Listener using your device's public key as a peer before proceeding to device configuration. See the README.md in the [wireguard-echo](../wireguard-echo/readme.md) folder for instructions, but be sure to use the public key generated above in place of one of the peer key parameters. The outputs of the wireguard-echo will include its public key, assigned domain and port (needed for the configuration below).

### 8. Configuration File Setup (alternate location)

Update the `config.txt` file in the project `data/` folder (PlatformIO) or include it in your LittleFS image for Arduino IDE.

```
# WiFi Configuration
ssid_name: YourNetworkName
ssid_psk: YourNetworkPassword

# WireGuard Configuration  
wg_private_key: <your 32 byte base64 device private key>
wg_peer_public_key: <your listener public key>
wg_peer_endpoint: <your listener domain>
wg_peer_port: <your listener port>
```

### 9. Upload and Test

1. **PlatformIO — Build, Upload filesystem, and Upload firmware** (recommended):

    - Build firmware:

       platformio run

    - Upload LittleFS filesystem (uploads contents of the `data/` folder to the board LittleFS):

       platformio run --target uploadfs

    - Upload firmware:

       platformio run --target upload

   You can also use the VS Code PlatformIO extension buttons: "Build", "Upload Filesystem image", and "Upload" to run these steps from the UI.

2. **Arduino IDE — Compile and Upload**:
    - Open the sketch in Arduino IDE
    - Use LittleFS uploader plugin or external tools to flash LittleFS image if needed
    - Click Upload (Ctrl+U)
    - Monitor Serial output (115200 baud)

2. **Verify Operation**:
   - Check serial output for connection progress
   - Verify display shows GUI elements
   - Test touch functionality (Send Data button and slider)
   - Confirm temperature readings

## Troubleshooting

### Common Issues

**Error: "LVGL: lv_conf.h is not set properly"**
- Solution: Ensure you're using the specific lv_conf.h file from Random Nerd Tutorials

**Error: "TFT_eSPI: User_Setup file not found"**
- Solution: Replace User_Setup.h with the CYD-specific version

**Error: "Filesystem initialization failed"**
- Ensure `config.txt` exists in LittleFS and is formatted correctly
- With PlatformIO: confirm files are in `data/` and run `platformio run --target uploadfs`

**Error: "WiFi connection failed"**
- Verify SSID and password in config.txt
- Check WiFi network availability
- Ensure 2.4GHz network (ESP32 doesn't support 5GHz)

**Error: "Temperature sensor not found"**
- Check wiring connections
- Verify 4.7kΩ pullup resistor is connected
- Test sensor with multimeter (should read ~3.3V on data line when idle)

**Error: "WireGuard initialization failed"**
- Verify private key format (44 characters, base64)
- Check server endpoint and port
- Ensure peer is configured on server side

**No Response from Listener**
- Verify private key format (44 characters, base64)
- Check server endpoint and port
- Ensure peer is configured on server side
- Check for Errors in your Destination logs

## Tips
1. **WiFi Range**: Ensure strong WiFi signal for stable connection
2. **Power Supply**: Use adequate 5V power supply (minimum 1A)
3. **Temperature Sensor**: Keep sensor wires short for reliable readings

## Next Steps

After successful installation:
1. Test data transmission through the VPN tunnel (device should connect automatically to the configured gateway)
2. Customize GUI elements as needed
3. Add additional sensors if desired
