cmake_minimum_required(VERSION 3.20)
project(mmod_native LANGUAGES CXX)

add_library(mmod_native SHARED
  src/session.cpp
  src/gpu_blend.cpp
)

target_include_directories(mmod_native
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(mmod_native
  PRIVATE
    MMOD_NATIVE_EXPORTS
    UNICODE
    _UNICODE
)

target_compile_features(mmod_native PUBLIC cxx_std_17)

if(MSVC)
  target_compile_options(mmod_native PRIVATE /W4 /permissive-)
endif()

set_target_properties(mmod_native PROPERTIES
  OUTPUT_NAME "mmod_native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
