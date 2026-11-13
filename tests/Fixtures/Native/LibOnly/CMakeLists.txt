cmake_minimum_required(VERSION 3.20)
project(OnlyLib VERSION 1.0.0 LANGUAGES C)
add_library(onlylib STATIC lib.c)
target_include_directories(onlylib PUBLIC include)
