cmake_minimum_required(VERSION 3.20)

project(eet
    VERSION 1.0.0
    DESCRIPTION "The EET virtual machine, C++20 runtime"
    LANGUAGES CXX)

# Single-config generators (Ninja, Makefiles) build unoptimised unless a type is named.
# Multi-config ones (Visual Studio, Xcode) ignore this and take --config instead, which is
# what tools/runtimes.py passes.
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# The harness looks for an executable called exactly "eet", in the build root or in a
# per-config subdirectory of it. Nothing outside the standard library is linked.
add_executable(eet
    src/main.cpp
    src/module.cpp
    src/sink.cpp
    src/trap.cpp
    src/vm.cpp)

target_include_directories(eet PRIVATE include)

# C++20 is load-bearing, not a preference: the runtime relies on the standard pinning
# right shift of a negative value to an arithmetic shift and signed conversion to
# two's complement, both of which were merely implementation-defined before C++20.
target_compile_features(eet PRIVATE cxx_std_20)
set_target_properties(eet PROPERTIES CXX_EXTENSIONS OFF)

if(MSVC)
    target_compile_options(eet PRIVATE /W4 /permissive- /utf-8)
else()
    target_compile_options(eet PRIVATE
        -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion -Wshadow)
endif()
