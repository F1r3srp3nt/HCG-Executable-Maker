cmake_minimum_required(VERSION 3.16)
project(LinuxPrimes VERSION 1.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

find_package(Threads REQUIRED)

add_subdirectory(core)

add_executable(primes src/main.c src/report.c)
target_include_directories(primes PRIVATE src)
target_link_libraries(primes PRIVATE primecore Threads::Threads m)
