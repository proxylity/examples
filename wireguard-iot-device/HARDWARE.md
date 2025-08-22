# Hardware Requirements

## Base Hardware

### Cheap Yellow Display (CYD) Board
- **Model**: ESP32-2432S028R development board
- **Display**: 2.8" TFT LCD (240x320 pixels) with resistive touchscreen
 - **Storage**: On-device LittleFS filesystem (used for configuration)
- **Connectivity**: Built-in WiFi and Bluetooth

For detailed pinout and board specifications, refer to the [Random Nerd Tutorials CYD documentation](https://RandomNerdTutorials.com/cyd-lvgl/).

## Required External Components

### DS18B20 Temperature Sensor
- **Type**: Digital temperature sensor with OneWire interface
- **Operating Range**: -55°C to +125°C (-67°F to +257°F)
- **Accuracy**: ±0.5°C from -10°C to +85°C
- **Package**: TO-92 or waterproof probe versions available

### Pull-up Resistor
- **Value**: 4.7kΩ (standard for OneWire bus)
- **Type**: 1/4 watt carbon film resistor
- **Tolerance**: 5% or better

## Wiring Configuration

### CM1 Connector Connections
The DS18B20 temperature sensor must be connected to the CM1 connector on the CYD board:

```
DS18B20 Pin Connections (TO-92 package, flat side facing you):
  Pin 1 (GND)  →  CM1 GND
  Pin 2 (DATA) →  CM1 GPIO 27 (also connect 4.7kΩ resistor to 3.3V)  
  Pin 3 (VDD)  →  CM1 3.3V

Pull-up Resistor:
  4.7kΩ resistor between DATA (Pin 2) and VDD (Pin 3)
```

### Wiring Diagram

```
DS18B20 Temperature Sensor Wiring:

    DS18B20 (TO-92 Package)
    Flat side facing you:
    
    Pin 1 (GND) ──────────────── CM1 GND
                                  
    Pin 2 (DATA) ────────┬────── CM1 GPIO 27
                         │
                    4.7kΩ Resistor
                         │
    Pin 3 (VDD) ─────────┴────── CM1 3.3V
```

### Alternative Waterproof Sensor Wiring

If using a waterproof DS18B20 probe with wire leads:

```
Wire Colors (standard):
  Red Wire    →  CM1 3.3V
  Black Wire  →  CM1 GND  
  Yellow Wire →  CM1 GPIO 27 (with 4.7kΩ pullup to 3.3V)
```

## Assembly Notes

1. **Sensor Placement**: The DS18B20 can be placed remotely from the display board - OneWire protocol supports cable lengths up to 100 meters with proper cabling.

2. **Pull-up Resistor**: The 4.7kΩ resistor is critical for reliable OneWire communication. It should be placed as close as possible to the sensor connection point.

3. **Multiple Sensors**: The OneWire bus supports multiple DS18B20 sensors on the same data line. Each sensor has a unique 64-bit address for identification.

4. **Power Options**: The DS18B20 can operate in "parasitic power" mode using only the data line for power, but normal power mode (as shown) is more reliable.

## Additional Components

### MicroSD Card
### Optional MicroSD Slot
The board includes a MicroSD slot. This project, however, stores configuration in the on-device LittleFS filesystem by default. To use the project's workflow, place `config.txt` in the PlatformIO `data/` folder and run `platformio run --target uploadfs` to write it to the device filesystem. If you prefer to use the SD card instead, modify the code accordingly.

### Built-in Sensors
The CYD board includes a built-in Light Dependent Resistor (LDR) that is used automatically for backlight adjustment - no external connections required.

## Power and Environmental Specifications

### Power Requirements
- **Input**: 5V via USB-C connector
- **Consumption**: ~200-300mA during normal operation
- **Peak Current**: ~500mA during WiFi transmission and sensor reading

### Operating Environment  
- **Temperature Range**: 0°C to 50°C (operational)
- **Humidity**: 10% to 85% non-condensing
- **DS18B20 Range**: -55°C to +125°C (sensor can exceed board limits)

## Troubleshooting Hardware Issues

### Temperature Sensor Not Detected
1. Verify 4.7kΩ pull-up resistor is connected between DATA and VDD
2. Check all connection points for continuity  
3. Ensure sensor is not damaged (test with multimeter)
4. Verify GPIO 27 connection to CM1 connector

### Erratic Temperature Readings
1. Check for loose connections
2. Ensure pull-up resistor value is correct (4.7kΩ)  
3. Keep sensor wires as short as practical
4. Use shielded cable for long runs
5. Prevent cat from chewing through wires

### Power Issues
1. Ensure adequate 5V power supply (minimum 1A capacity)
2. Check USB cable quality for voltage drop
3. Monitor for voltage sags during WiFi transmission
