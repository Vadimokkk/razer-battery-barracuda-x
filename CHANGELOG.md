# Changelog

## 1.3.2

- Show the current charge percentage as digits in the system tray battery icon.
- Keep the charging bolt visible beside the number and color the icon by battery level.

## 1.3.1

- Read exact battery level and active charging notifications from the `1532:0550` dongle, preventing the charging-voltage jump from appearing as a battery percentage increase.
- Show the headset's active charging state in the dashboard and tray.
- Preserve discharge samples across charging sessions so runtime and drain estimates can build from history.
- Fix clipped dashboard text for charging and saved-level states.

## 1.3.0

- Read the headset's reported battery percentage and charging-cable state through the 2.4 GHz dongle instead of estimating both from voltage when supported.
- Keep the charge percentage visible with a blue charging ring and tray indicator while the cable is connected.
- Calculate battery drain and estimated runtime from recent local discharge history, preserving useful samples across restarts.

## 1.2.0

- Added a local charge history graph with 24-hour and 7-day ranges.
- Added Windows default microphone mute/unmute in the tray menu.
- Shortened and resized dashboard labels to prevent clipping.

## 1.1.0

- Reworked the battery dashboard and added a runtime estimate based on observed battery drain.
