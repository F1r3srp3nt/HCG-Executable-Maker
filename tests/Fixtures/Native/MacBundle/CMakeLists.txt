cmake_minimum_required(VERSION 3.20)
project(MacHello VERSION 1.2.0 LANGUAGES C)

# On macOS this target is an application bundle (MacHello.app) with the Info.plist CMake generates from these
# properties; on other platforms MACOSX_BUNDLE is ignored and it is a plain executable.
add_executable(MacHello MACOSX_BUNDLE main.c)
set_target_properties(MacHello PROPERTIES
  C_STANDARD 11
  MACOSX_BUNDLE_BUNDLE_NAME "Mac Hello"
  MACOSX_BUNDLE_GUI_IDENTIFIER "com.example.machello"
  MACOSX_BUNDLE_BUNDLE_VERSION "1.2.0"
  MACOSX_BUNDLE_SHORT_VERSION_STRING "1.2.0")
