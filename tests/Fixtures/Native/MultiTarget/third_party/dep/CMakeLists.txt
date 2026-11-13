# A vendored dependency with its own project(): part of Multi, never a separate application.
project(Dep LANGUAGES C)
add_library(dep STATIC dep.c)
