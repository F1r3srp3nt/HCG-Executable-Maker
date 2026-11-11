add_library(primecore STATIC sieve.c)
target_include_directories(primecore PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
