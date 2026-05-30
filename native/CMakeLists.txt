# basis_block_encoder native library.
#
# Builds a single shared library (basis_block_encoder) from this shim plus the one source file
# from basis_universal's transcoder module. basis_universal is fetched at a *pinned* commit because
# the encoder functions we call are internal APIs that may change between revisions.
#
# Output: basis_block_encoder.dll / libbasis_block_encoder.so / libbasis_block_encoder.dylib
#
# Requires CMake >= 3.14 (FetchContent) and a C++17 compiler.

cmake_minimum_required(VERSION 3.14)
project(basis_block_encoder LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Build a clean shared library: position-independent, and export only our C API
# (everything from basis_universal stays hidden).
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# Default to a fully-optimized build. Release implies -O3/-O2 and (importantly) -DNDEBUG, which
# strips the many asserts basis_universal sprinkles through the encoder inner loops.
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

option(BBE_NATIVE_ARCH
  "Tune for the building machine's CPU (-march=native). Faster, but the binary is NOT portable - leave OFF for distributable packages."
  OFF)

# --- basis_universal (pinned) -----------------------------------------------------------------
set(BASISU_GIT_REPOSITORY "https://github.com/BinomialLLC/basis_universal.git"
    CACHE STRING "basis_universal repository URL")
set(BASISU_GIT_TAG "e4f439fc9545b6a9e1fd26fc7ffd0c682c4b96d4"
    CACHE STRING "basis_universal commit to build against (pinned: internal encoder APIs may change)")

# Allow pointing at a local checkout instead of fetching (e.g. a vendored copy or submodule):
set(BASISU_SOURCE_DIR "" CACHE PATH "Path to an existing basis_universal checkout (optional)")

if(BASISU_SOURCE_DIR)
  set(basisu_SOURCE_DIR "${BASISU_SOURCE_DIR}")
  message(STATUS "basis_block_encoder: using basis_universal from ${basisu_SOURCE_DIR}")
else()
  include(FetchContent)
  FetchContent_Declare(basisu
    GIT_REPOSITORY "${BASISU_GIT_REPOSITORY}"
    GIT_TAG        "${BASISU_GIT_TAG}"
  )
  # Populate the source only; do NOT add_subdirectory() - we compile a single TU ourselves and
  # don't want basis_universal's own CMake project / tools.
  FetchContent_GetProperties(basisu)
  if(NOT basisu_POPULATED)
    FetchContent_Populate(basisu)
  endif()
  message(STATUS "basis_block_encoder: fetched basis_universal @ ${BASISU_GIT_TAG}")
endif()

set(BASISU_TRANSCODER_DIR "${basisu_SOURCE_DIR}/transcoder")
if(NOT EXISTS "${BASISU_TRANSCODER_DIR}/basisu_transcoder.cpp")
  message(FATAL_ERROR
    "Could not find basisu_transcoder.cpp under ${BASISU_TRANSCODER_DIR}. "
    "Check BASISU_SOURCE_DIR or the pinned commit.")
endif()

# --- the library ------------------------------------------------------------------------------
add_library(basis_block_encoder SHARED
  "${CMAKE_CURRENT_SOURCE_DIR}/src/basis_block_encoder.cpp"
  "${BASISU_TRANSCODER_DIR}/basisu_transcoder.cpp"
)

target_include_directories(basis_block_encoder PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "${BASISU_TRANSCODER_DIR}"
)

# We only use the block encoders, never the .basis/KTX2 container paths - turning KTX2 off keeps
# the build free of the optional zstd dependency. UASTC / XUASTC / UASTC_HDR stay at their defaults
# (on); those are what compile and initialize the bc7f and BC6H encoders we expose.
#
# Note: basis_universal also has an experimental SSE4.1 path for the bc7f encoder, but it is
# hard-disabled upstream (#define BASISU_BC7F_USE_SSE41 (0), "only very minimally tested",
# ~10% gain) and cannot be toggled from the build, so we leave the tested scalar path in place.
target_compile_definitions(basis_block_encoder PRIVATE
  BASISD_SUPPORT_KTX2=0
  BASISD_SUPPORT_KTX2_ZSTD=0
)

# --- max-performance code generation ----------------------------------------------------------
# Detect a 32-bit x86 target (distinct from 32-bit ARM): there SSE2 is not the default ABI, so we
# enable it explicitly for the scalar-float speedup (and to match the 64-bit baseline).
set(BBE_IS_X86_32 FALSE)
if(CMAKE_SIZEOF_VOID_P EQUAL 4 AND CMAKE_SYSTEM_PROCESSOR MATCHES "(i[3-6]86|[xX]86|amd64|AMD64|x86_64)")
  set(BBE_IS_X86_32 TRUE)
endif()

if(MSVC)
  # Release already supplies /O2 /DNDEBUG; /EHsc for the C++ runtime, and SSE2 on 32-bit x86.
  target_compile_options(basis_block_encoder PRIVATE /O2 /EHsc)
  target_compile_definitions(basis_block_encoder PRIVATE _CRT_SECURE_NO_WARNINGS)
  if(CMAKE_SIZEOF_VOID_P EQUAL 4)
    target_compile_options(basis_block_encoder PRIVATE /arch:SSE2)
  endif()
else()
  target_compile_options(basis_block_encoder PRIVATE -O3)
  if(BBE_IS_X86_32)
    target_compile_options(basis_block_encoder PRIVATE -msse2 -mfpmath=sse)
  endif()
  if(BBE_NATIVE_ARCH)
    target_compile_options(basis_block_encoder PRIVATE -march=native)
  endif()
endif()

# Link-time / interprocedural optimization (for Release configs), so the per-block gather inlines
# into the encoder calls across the shim/transcoder boundary. Skipped automatically if unsupported.
include(CheckIPOSupported)
check_ipo_supported(RESULT BBE_IPO_SUPPORTED OUTPUT BBE_IPO_MESSAGE LANGUAGES CXX)
if(BBE_IPO_SUPPORTED)
  set_property(TARGET basis_block_encoder PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
  set_property(TARGET basis_block_encoder PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELWITHDEBINFO ON)
  message(STATUS "basis_block_encoder: LTO/IPO enabled for optimized configs")
else()
  message(STATUS "basis_block_encoder: LTO/IPO not available (${BBE_IPO_MESSAGE})")
endif()

# std::call_once needs a threading backend on most *nix toolchains.
set(THREADS_PREFER_PTHREAD_FLAG ON)
find_package(Threads QUIET)
if(Threads_FOUND)
  target_link_libraries(basis_block_encoder PRIVATE Threads::Threads)
endif()

# No "lib" prefix juggling needed; consumers load by the platform-conventional name that
# LibraryImport("basis_block_encoder") resolves to.
install(TARGETS basis_block_encoder
  RUNTIME DESTINATION bin
  LIBRARY DESTINATION lib
  ARCHIVE DESTINATION lib)
