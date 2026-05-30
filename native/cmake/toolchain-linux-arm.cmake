# CMake toolchain for cross-compiling to 32-bit ARM Linux (ARMv7 hard-float, RID: linux-arm).
# Requires the g++-arm-linux-gnueabihf cross toolchain:
#   sudo apt-get install -y g++-arm-linux-gnueabihf
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER   arm-linux-gnueabihf-gcc)
set(CMAKE_CXX_COMPILER arm-linux-gnueabihf-g++)

# Find headers/libs in the cross sysroot, but run host programs (e.g. git for FetchContent).
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
