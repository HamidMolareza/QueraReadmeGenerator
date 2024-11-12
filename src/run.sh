#!/bin/bash

docker_name="${1:-quera-readme-generator}"

clear

docker rmi "$docker_name" -f
docker compose up --build --pull never --abort-on-container-exit