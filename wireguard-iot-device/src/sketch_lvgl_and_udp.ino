/*  Install the "lvgl" library version 9.2 by kisvegabor to interface with the TFT Display - https://lvgl.io/
    *** IMPORTANT: lv_conf.h available on the internet will probably NOT work with the examples available at Random Nerd Tutorials ***
    *** YOU MUST USE THE lv_conf.h FILE PROVIDED IN THE LINK BELOW IN ORDER TO USE THE EXAMPLES FROM RANDOM NERD TUTORIALS ***
    FULL INSTRUCTIONS AVAILABLE ON HOW CONFIGURE THE LIBRARY: https://RandomNerdTutorials.com/cyd-lvgl/ or https://RandomNerdTutorials.com/esp32-tft-lvgl/   */
#include <lvgl.h>

/*  Install the "TFT_eSPI" library by Bodmer to interface with the TFT Display - https://github.com/Bodmer/TFT_eSPI
    *** IMPORTANT: User_Setup.h available on the internet will probably NOT work with the examples available at Random Nerd Tutorials ***
    *** YOU MUST USE THE User_Setup.h FILE PROVIDED IN THE LINK BELOW IN ORDER TO USE THE EXAMPLES FROM RANDOM NERD TUTORIALS ***
    FULL INSTRUCTIONS AVAILABLE ON HOW CONFIGURE THE LIBRARY: https://RandomNerdTutorials.com/cyd-lvgl/ or https://RandomNerdTutorials.com/esp32-tft-lvgl/   */
#include <TFT_eSPI.h>

// Install the "XPT2046_Touchscreen" library by Paul Stoffregen to use the Touchscreen - https://github.com/PaulStoffregen/XPT2046_Touchscreen
#include <XPT2046_Touchscreen.h>

// Install OneWire and DallasTemperature libraries
#include <OneWire.h>
#include <DallasTemperature.h>

// Install WireGuard library
#include <WireGuard-ESP32.h>
#include "esp_log.h"

// Inlcude WiFi and UDP librariers
#include "WiFi.h"
#include "WiFiUDP.h"

// Use LittleFS for config file
#include <SD.h>
#include <LittleFS.h>

/// D E F I N E S

// LittleFS is used to store configuration on-device (no SD card required).

// Touchscreen pins
#define XPT2046_IRQ 36   // T_IRQ
#define XPT2046_MOSI 32  // T_DIN
#define XPT2046_MISO 39  // T_OUT
#define XPT2046_CLK 25   // T_CLK
#define XPT2046_CS 33    // T_CS

#define SCREEN_WIDTH 240
#define SCREEN_HEIGHT 320

#define DRAW_BUF_SIZE (SCREEN_WIDTH * SCREEN_HEIGHT / 10 * (LV_COLOR_DEPTH / 8))

#define LDR_VALUE_COUNT 3

#define LCD_BACK_LIGHT_PIN 21

// use first channel of 16 channels (started from zero)
#define LEDC_CHANNEL_0     0

// use 12 bit precission for LEDC timer
#define LEDC_TIMER_12_BIT  12

// use 5000 Hz as a LEDC base frequency
#define LEDC_BASE_FREQ     5000

// analog pin for the light dependent resistor
#define LDR_PIN            34

// one-wire pin for temp sensor
#define ONEWIRE_PIN 27
#define TEMP_ARC_MIN -4
#define TEMP_ARC_MAX 104

// UDP and sensor settings
#define UDP_BUFFER_SIZE 1024
#define MAX_SEND_DELAY_MS 4294967295UL  // about 50 days
#define TEMPERATURE_UPDATE_INTERVAL 5000
#define SENSOR_READ_DELAY 20

// device state tracking

#define STATE_INIT 0
#define STATE_CONFIG_LOADED 5
#define STATE_WIFI_BEGIN 10
#define STATE_WIFI_CONNECTED 15
#define STATE_TIME_CONFIG 20
#define STATE_TIME_CONFIGURED 25
#define STATE_WIREGUARD_BEGIN 30
#define STATE_WIREGUARD_CONNECTED 35
#define STATE_UDP_BEGIN 40
#define STATE_UDP_CONNECTED 45
#define STATE_READY 50
#define STATE_ERROR -1

/// S T A T I C S

String ssid = "********";
String psk = "********";

// wireguard
String wg_private_key = "*******************************************=";
IPAddress wg_local_vpn_ip(10, 0, 0, 2);
int local_vpn_port = 10101;

String wg_peer_public_key = "*******************************************=";
String wg_peer_endpoint = "endpoint.example.tld";
int wg_peer_port = 1234;
 
IPAddress remote_vpn_ip(10, 10, 10, 10);
int remote_vpn_port = 1010;

bool wg_connected = false;

String public_ntp_server = "pool.ntp.org";
String wg_ntp_server = "10.10.10.10";

unsigned long last_ntp_sync = millis();
unsigned long ntp_sync_interval = 15 * 60 * 1000; // 15 minutes

// Touchscreen coordinates: (x, y) and pressure (z)
int x, y, z;

uint32_t draw_buf[DRAW_BUF_SIZE / 4];

bool send_data = false;
unsigned long send_interval = 5000;
unsigned long last_send = millis();

unsigned long last_tick = millis();

unsigned char buffer[1024];
String udp_msg;
String error_msg;

OneWire oneWire(ONEWIRE_PIN);
DallasTemperature sensors(&oneWire);

float current_temp = 0.0;
int ldr_values[LDR_VALUE_COUNT] = {0};
int ldr_index = 0;
int smooth_ldr = 0;
int lux;

const char degree_symbol[] = "\u00B0";

bool temperatureInProgress = true; // effectively delays the first sample until `last_sensor_update + sensor_update_interval`
unsigned long last_sensor_update = millis();
unsigned long sensor_update_interval = 5000;

SPIClass touchscreenSPI = SPIClass(VSPI);
XPT2046_Touchscreen touchscreen(XPT2046_CS, XPT2046_IRQ);

WireGuard wg;
WiFiUDP Udp;

int state = STATE_INIT;

// L V G L

// thermo
static lv_obj_t* arc;
static lv_obj_t* slider_label;
static lv_obj_t * text_label = NULL;
static lv_obj_t * text_label_temp_value = NULL;

// clock
static lv_obj_t * scale;
static lv_obj_t * hour_hand;
static lv_obj_t * minute_hand;
static lv_obj_t * second_hand;
static lv_point_precise_t hour_hand_points[2];
static lv_point_precise_t minute_hand_points[2];
static lv_point_precise_t second_hand_points[2];

/// M E T H O D S

void ledcAnalogWrite(uint8_t channel, uint32_t value, uint32_t valueMax = 255) {
  uint32_t duty = (4095 / valueMax) * min(value, valueMax);
  ledcWrite(channel, duty);
}

static void clock_cb(lv_timer_t * timer)
{
    LV_UNUSED(timer);

    if (state < STATE_READY) 
      return;

    struct tm timeinfo;
    if(!getLocalTime(&timeinfo)){
      Serial.println("Failed to obtain time in clock_cb");
      return;
    }

    lv_scale_set_line_needle_value(scale, second_hand, 40, timeinfo.tm_sec);
    lv_scale_set_line_needle_value(scale, minute_hand, 40, timeinfo.tm_min);
    lv_scale_set_line_needle_value(scale, hour_hand, 30, (timeinfo.tm_hour > 12 ? timeinfo.tm_hour - 12 : timeinfo.tm_hour) * 5 + (timeinfo.tm_min / 12));
}

void touchscreen_read(lv_indev_t * indev, lv_indev_data_t * data) {
  // Checks if Touchscreen was touched, and prints X, Y and Pressure (Z)
  if(touchscreen.tirqTouched() && touchscreen.touched()) {
    // Get Touchscreen points
    TS_Point p = touchscreen.getPoint();
    // Calibrate Touchscreen points with map function to the correct width and height
    x = map(p.x, 200, 3700, 1, SCREEN_WIDTH);
    y = map(p.y, 240, 3800, 1, SCREEN_HEIGHT);
    z = p.z;

    data->state = LV_INDEV_STATE_PRESSED;

    // Set the coordinates
    data->point.x = x;
    data->point.y = y;
  }
  else {
    data->state = LV_INDEV_STATE_RELEASED;
  }
}

// Callback that is triggered when btn2 is clicked/toggled
static void event_handler_btn2(lv_event_t * e) {
  lv_event_code_t code = lv_event_get_code(e);
  lv_obj_t * obj = (lv_obj_t*) lv_event_get_target(e);
  if(code == LV_EVENT_VALUE_CHANGED) {
    LV_UNUSED(obj);
    LV_LOG_USER("Toggled %s", lv_obj_has_state(obj, LV_STATE_CHECKED) ? "on" : "off");

    send_data = lv_obj_has_state(obj, LV_STATE_CHECKED);
  }
}

// Callback that prints the current slider value on the TFT display and Serial Monitor for debugging purposes
static void slider_event_callback(lv_event_t * e) {
  lv_obj_t * slider = (lv_obj_t*) lv_event_get_target(e);
  char buf[8];
  int v = (int)lv_slider_get_value(slider);
  send_interval = v * 1000;
  lv_snprintf(buf, sizeof(buf), "%d", v);
  lv_label_set_text(slider_label, buf);
  lv_obj_align_to(slider_label, slider, LV_ALIGN_OUT_BOTTOM_MID, 0, 10);
  LV_LOG_USER("Slider changed to %d%%", (int)lv_slider_get_value(slider));
}

void lv_create_main_gui(void) {
  text_label = lv_label_create(lv_screen_active());
  lv_label_set_long_mode(text_label, LV_LABEL_LONG_WRAP);    // Breaks the long lines
  lv_label_set_text(text_label, "Proxylity Ping!");
  lv_obj_set_style_text_align(text_label, LV_TEXT_ALIGN_CENTER, 0);
  lv_obj_align(text_label, LV_ALIGN_CENTER, 0, -90);

  // Create a Toggle button (btn2)
  lv_obj_t * btn2 = lv_button_create(lv_screen_active());
  lv_obj_add_event_cb(btn2, event_handler_btn2, LV_EVENT_ALL, NULL);
  lv_obj_align(btn2, LV_ALIGN_CENTER, 0, 10);
  lv_obj_add_flag(btn2, LV_OBJ_FLAG_CHECKABLE);
  lv_obj_set_height(btn2, LV_SIZE_CONTENT);

  lv_obj_t* btn_label = lv_label_create(btn2);
  lv_label_set_text(btn_label, "Send Data");
  lv_obj_center(btn_label);
  
  // Create a slider aligned in the center bottom of the TFT display
  lv_obj_t * slider = lv_slider_create(lv_screen_active());
  lv_obj_align(slider, LV_ALIGN_CENTER, 0, 60);
  lv_obj_add_event_cb(slider, slider_event_callback, LV_EVENT_VALUE_CHANGED, NULL);
  lv_slider_set_range(slider, 1, 60);
  lv_obj_set_style_anim_duration(slider, 2000, 0);

  // Create a label below the slider to display the current slider value
  slider_label = lv_label_create(lv_screen_active());
  String label("");
  label += (send_interval / 1000);
  lv_label_set_text(slider_label, label.c_str());
  lv_obj_align_to(slider_label, slider, LV_ALIGN_OUT_BOTTOM_MID, 0, 10);

  // Create an Arc to display temperatire
  arc = lv_arc_create(lv_screen_active());
  lv_obj_set_size(arc, 92, 92);
  lv_arc_set_rotation(arc, 135);
  lv_arc_set_bg_angles(arc, 0, 270);
  // lv_obj_set_style_arc_color(arc, lv_color_hex(0x666666), LV_PART_INDICATOR);
  // lv_obj_set_style_bg_color(arc, lv_color_hex(0x333333), LV_PART_KNOB);
  lv_obj_align(arc, LV_ALIGN_RIGHT_MID, -10, -20);

  // Create a text label in font size 32 to display the latest temperature reading
  text_label_temp_value = lv_label_create(arc);
  lv_label_set_text(text_label_temp_value, "--.--");
  lv_obj_align(text_label_temp_value, LV_ALIGN_CENTER, 0, 0);
  // static lv_style_t style_temp;
  // lv_style_init(&style_temp);
  // lv_style_set_text_font(&style_temp, &lv_font_montserrat_32);
  // lv_obj_add_style(text_label_temp_value, &style_temp, 0);

  // create the clock
  scale = lv_scale_create(lv_screen_active());
  lv_obj_set_size(scale, 92, 92);
  lv_obj_align(scale, LV_ALIGN_LEFT_MID, 10, -20);
  lv_scale_set_mode(scale, LV_SCALE_MODE_ROUND_INNER);
  lv_scale_set_label_show(scale, true);

  lv_scale_set_total_tick_count(scale, 61);
  lv_scale_set_major_tick_every(scale, 5);

  static const char * hour_ticks[] = {"12", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", NULL};
  lv_scale_set_text_src(scale, hour_ticks);

  lv_scale_set_range(scale, 0, 60);
  lv_scale_set_angle_range(scale, 360);
  lv_scale_set_rotation(scale, 270);

  second_hand = lv_line_create(scale);
  lv_line_set_points_mutable(second_hand, second_hand_points, 2);
  lv_obj_set_style_line_width(second_hand, 1, 0);
  lv_obj_set_style_line_rounded(second_hand, true, 0);
  lv_obj_set_style_line_color(second_hand, lv_palette_main(LV_PALETTE_RED), 0);

  minute_hand = lv_line_create(scale);
  lv_line_set_points_mutable(minute_hand, minute_hand_points, 2);
  lv_obj_set_style_line_width(minute_hand, 3, 0);
  lv_obj_set_style_line_rounded(minute_hand, true, 0);

  hour_hand = lv_line_create(scale);
  lv_obj_set_style_line_width(hour_hand, 5, 0);
  lv_obj_set_style_line_rounded(hour_hand, true, 0);

/* Major tick properties */
  static lv_style_t major_ticks_style;
  lv_style_init(&major_ticks_style);
  lv_style_set_length(&major_ticks_style, 6); /* tick length */
  lv_style_set_line_width(&major_ticks_style, 3); /* tick width */
  lv_obj_add_style(scale, &major_ticks_style, LV_PART_INDICATOR);

  /* Minor tick properties */
  static lv_style_t minor_ticks_style;
  lv_style_init(&minor_ticks_style);
  lv_style_set_length(&minor_ticks_style, 4); /* tick length */
  lv_style_set_line_width(&minor_ticks_style, 1); /* tick width */
  lv_obj_add_style(scale, &minor_ticks_style, LV_PART_ITEMS);

  /* Main line properties */
  static lv_style_t main_line_style;
  lv_style_init(&main_line_style);
  lv_style_set_arc_width(&main_line_style, 5);
  lv_style_set_arc_color(&main_line_style, lv_palette_main(LV_PALETTE_BLUE));
  lv_obj_add_style(scale, &main_line_style, LV_PART_MAIN);

  lv_timer_t * timer = lv_timer_create(clock_cb, 333, NULL);
  lv_timer_ready(timer);
}

// function to print a device address
void printAddress(DeviceAddress deviceAddress) {
  for (uint8_t i = 0; i < 8; i++){
    if (deviceAddress[i] < 16) Serial.print("0");
      Serial.print(deviceAddress[i], HEX);
  }
}

void read_config() {
  if (!LittleFS.begin()) {
  Serial.println(error_msg = "LittleFS initialization failed!");
    state = STATE_ERROR;
    return;
  }

  // Open config file
  File configFile = LittleFS.open("/config.txt");
  if (!configFile) {
    Serial.println(error_msg = "Failed to open file /config.txt.");
    state = STATE_ERROR;
    return;
  }

  String line, key, value;
  while (configFile.available()) {
    line = configFile.readStringUntil('\n');
    line.trim();
    if (line.startsWith("#") || line.length() == 0) continue; // comments, empty lines
    int colonIndex = line.indexOf(':');
    if (colonIndex != -1) {
      key = line.substring(0, colonIndex);
      key.trim();
      
      value = line.substring(colonIndex + 1);
      value.trim();

      Serial.println("CONFIG: Found key " + key);
      if (key == "ssid_name") {
          ssid = value;
      } else if (key == "ssid_psk") {
          psk = value;
      } else if (key == "wg_private_key") {
        wg_private_key = value;
      } else if (key == "wg_peer_public_key") {
        wg_peer_public_key = value;
      } else if (key == "wg_peer_endpoint") {
        wg_peer_endpoint = value;
      } else if (key == "wg_peer_port") {
        wg_peer_port = value.toInt();
      } else Serial.println("Skipping bad config key: " + key);
    } else Serial.println("Skiping bad config line: " + line);
  }
  configFile.close();
  LittleFS.end();
  Serial.println("Config loaded, LittleFS ended.");
}

void start_wifi() {
  WiFi.mode(WIFI_STA);
  delay(200);
  if (WiFi.status() != WL_CONNECTED) {
    String s = "Connecting to ";
    s += ssid;
    s += "...";
    lv_label_set_text(text_label, s.c_str());
    WiFi.begin(ssid, psk);
  }
  state = STATE_WIFI_BEGIN;
}

void wifi_connected() {
  Serial.println("WiFi connected");
  Serial.print("IP address: ");
  Serial.println(WiFi.localIP());
  
  // Set the text label to show the connected WiFi SSID
  String s = "Connected to ";
  s += ssid;
  s += ".";
  lv_label_set_text(text_label, s.c_str());
  
  // Set the state to indicate WiFi is connected
  state = STATE_WIFI_CONNECTED;
}

void synchronize_time() {
  if (wg_connected) {
    configTime(0, 0, wg_ntp_server.c_str());
  } else {
    configTime(0, 0, public_ntp_server.c_str());
  }
  setenv("TZ","PST8PDT,M3.2.0,M11.1.0",1);
  tzset();

  struct tm timeinfo;
  if(!getLocalTime(&timeinfo)){
    Serial.println("Failed to obtain time in clock_cb");
  }
  last_ntp_sync = millis();
}

void configure_time() {
  synchronize_time();

  String s = "Time synchronized.";
  lv_label_set_text(text_label, s.c_str());

  state = STATE_TIME_CONFIGURED;
}

void start_wireguard() {
  if (wg.begin(
    wg_local_vpn_ip,     // IP address of the local VPN interface
    wg_private_key.c_str(),        // Private key of the local interface
    wg_peer_endpoint.c_str(),   // Address of the endpoint peer.
    wg_peer_public_key.c_str(), // Public key of the endpoint peer.
    wg_peer_port)) 
  {
    Serial.println("WireGuard setup began.");
    state = STATE_WIREGUARD_BEGIN;
  } else {
    Serial.println(error_msg = "WireGuard failed to begin.");
    state = STATE_ERROR;
  }
}

void wireguard_connected() {
  String s = "WireGuard on ";
  s += ssid;
  s += ".";
  lv_label_set_text(text_label, s.c_str());
  wg_connected = true;
  state = STATE_WIREGUARD_CONNECTED;
}

void start_udp() {
  Udp.begin(local_vpn_port);
  Serial.print("UDP started on port ");
  Serial.println(local_vpn_port);
  
  // Set the state to indicate UDP is ready
  state = STATE_UDP_BEGIN;
}

void receive_udp() {
  int c = 0;
  while ((c = Udp.parsePacket()) > 0) {
    Serial.print("[ ");
    Serial.print(millis());
    Serial.print(" ] ");
    Serial.print("Received: ");

    int l = Udp.read(buffer, 1024);
    if (l > 0) {
      buffer[l] = 0;
      Serial.write(buffer, l);
    } else {
      Serial.print("<empty packet>");
    }
    Serial.print(" from ");
    Serial.print(Udp.remoteIP());
    Serial.print(":");
    Serial.println(Udp.remotePort());
  }
}

void send_udp() {
  udp_msg = String(current_temp) + degree_symbol;
  udp_msg += ", ";
  udp_msg += smooth_ldr;
  udp_msg += "ldr";

  Serial.print("[ ");
  Serial.print(millis());
  Serial.print(" ] ");
  Serial.print("Sending ");
  Serial.print(udp_msg);
  Serial.print(" to ");
  Serial.print(remote_vpn_ip);
  Serial.print(":");
  Serial.println(remote_vpn_port);

  Udp.beginPacket(remote_vpn_ip, remote_vpn_port);
  Udp.print(udp_msg);
  Udp.endPacket();

  last_send = millis();
}

void start_sensors() {
  // start reading the temperature sensor (asynchronous)
  sensors.requestTemperatures();
  temperatureInProgress = true;

  // read LDR and adjust the backlight
  ldr_values[ldr_index++ % LDR_VALUE_COUNT] = analogRead(LDR_PIN);
  smooth_ldr = 0;
  for (int i = 0; i < LDR_VALUE_COUNT; ++i)
    smooth_ldr += ldr_values[i];
  smooth_ldr /= LDR_VALUE_COUNT;
  ledcAnalogWrite(LEDC_CHANNEL_0, min(max(100, 4095 - smooth_ldr), 2048), 4095);

  lux = smooth_ldr > 0 ? (int)(500 / pow(smooth_ldr, .71)) : 500;

  last_sensor_update = millis();
}

void temperature_complete() {
  current_temp = sensors.getTempFByIndex(0);
  String t = String(current_temp) + degree_symbol;

  Serial.print("Temperature value: ");
  Serial.println(t);

  // update the temp display
  lv_arc_set_value(arc, map(int(current_temp), TEMP_ARC_MIN, TEMP_ARC_MAX, 0, 100));
  lv_label_set_text((lv_obj_t*) text_label_temp_value, t.c_str());

  temperatureInProgress = false;
}

void setup() {
  Serial.begin(115200);
  while (!Serial) delay(100);
  Serial.setDebugOutput(true);

  Serial.println("Loading config...)");
  read_config();

  String LVGL_Arduino = String("LVGL Library Version: ") + lv_version_major() + "." + lv_version_minor() + "." + lv_version_patch();
  Serial.println(LVGL_Arduino);
  
  // Start LVGL
  lv_init();

  // // Register print function for debugging
  // lv_log_register_print_cb(log_print);

  // Start the SPI for the touchscreen and init the touchscreen
  touchscreenSPI.begin(XPT2046_CLK, XPT2046_MISO, XPT2046_MOSI, XPT2046_CS);
  touchscreen.begin(touchscreenSPI);
  
  // Set the Touchscreen rotation in landscape mode
  // Note: in some displays, the touchscreen might be upside down, so you might need to set the rotation to 0: touchscreen.setRotation(0);
  touchscreen.setRotation(2);

  // Create a display object
  lv_display_t * disp;
  // Initialize the TFT display using the TFT_eSPI library
  disp = lv_tft_espi_create(SCREEN_WIDTH, SCREEN_HEIGHT, draw_buf, sizeof(draw_buf));
  lv_display_set_rotation(disp, LV_DISPLAY_ROTATION_90);
    
  // Initialize an LVGL input device object (Touchscreen)
  lv_indev_t * indev = lv_indev_create();
  lv_indev_set_type(indev, LV_INDEV_TYPE_POINTER);
  // Set the callback function to read Touchscreen input
  lv_indev_set_read_cb(indev, touchscreen_read);

  // Function to draw the GUI (text, buttons and sliders)
  lv_create_main_gui();

  // Setting up the LEDC and configuring the Back light pin
  // NOTE: this needs to be done after tft.init()
#if ESP_IDF_VERSION_MAJOR == 5
  ledcAttach(LCD_BACK_LIGHT_PIN, LEDC_BASE_FREQ, LEDC_TIMER_12_BIT);
#else
  ledcSetup(LEDC_CHANNEL_0, LEDC_BASE_FREQ, LEDC_TIMER_12_BIT);
  ledcAttachPin(LCD_BACK_LIGHT_PIN, LEDC_CHANNEL_0);
#endif
  ledcAnalogWrite(LEDC_CHANNEL_0, 2048, 4095); // On half brightness

  // setup reading LDR with fine attenuation
  analogSetPinAttenuation(LDR_PIN, ADC_0db);
  pinMode(LDR_PIN, INPUT);

  // start the templ sensor
  sensors.begin();
  sensors.setWaitForConversion(false); // non-blocking
  // sensors.setResolution(sensorAddress, 9);

  DeviceAddress tempDeviceAddress; 
  if(sensors.getAddress(tempDeviceAddress, 0)) {
    Serial.print("Found temp sensor address: ");
    printAddress(tempDeviceAddress);
    Serial.println();
  } else {
    Serial.println("Found ghost device. Check power and cabling");
  }

  Serial.println("Setup done");
}

void loop() {
  lv_task_handler();  // let the GUI do its work
  lv_tick_inc(millis() - last_tick); // tell LVGL how much time has passed
  last_tick = millis();

  switch (state) {
    case STATE_INIT: 
      Serial.println("State: STATE_INIT (starting WiFi)");
      start_wifi();
      break;

    case STATE_WIFI_BEGIN:
      Serial.println("State: WIFI_BEGIN");
      if (!WiFi.isConnected()) {
        delay(200);
        break; // wait until WiFi is connected
      }
      wifi_connected();
      break;

    case STATE_WIFI_CONNECTED:
      Serial.println("State: WIFI_CONNECTED");
      state = STATE_TIME_CONFIG;
      break;

    case STATE_TIME_CONFIG:
      Serial.println("State: STATE_TIME_CONFIG");
      configure_time();
      break;

    case STATE_TIME_CONFIGURED:
      Serial.println("State: STATE_TIME_CONFIGURED");
      start_wireguard();
      break;

    case STATE_WIREGUARD_BEGIN:
      Serial.println("State: WIREGUARD_BEGIN");
      if (!wg.is_initialized()) break; // wait until WireGuard is initialized
      wireguard_connected();
      break;

    case STATE_WIREGUARD_CONNECTED:
      Serial.println("State: WIREGUARD_CONNECTED");
      start_udp();
      break;

    case STATE_UDP_BEGIN:
      Serial.println("State: UDP_BEGIN");
      state = STATE_UDP_CONNECTED;
      break;

    case STATE_UDP_CONNECTED:
      Serial.println("State: UDP_CONNECTED");
      state = STATE_READY;
      break;

    case STATE_READY:
      receive_udp();
      if (send_data && millis() - last_send > send_interval) {
        send_udp();
      }
      if (millis() - last_ntp_sync > ntp_sync_interval) {
        synchronize_time();
      }
      break;

    case STATE_ERROR:
      lv_label_set_text(text_label, error_msg.c_str());
      delay(200);
      break;

    default:
      Serial.println("Unknown state!");
      delay(1000);
      break;
  }

  if (temperatureInProgress && sensors.isConversionComplete()) {
    temperature_complete();
  }

  // sample as quickly as possible, restart if delayed
  if (!temperatureInProgress || millis() - last_sensor_update > sensor_update_interval) {
    start_sensors();
  }

  delay(20); // give some time to the system
}